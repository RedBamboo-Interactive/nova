using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Leaf.Sdk;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Shared event-injection pipeline: persists the event as a discussion message,
/// broadcasts it to the UI (via the plugin-event → WebSocket bridge), and forwards
/// non-system events into the live RedCompute session. An event that arrives before the
/// discussion has a session is queued durably and delivered once the session exists.
/// Used by the /event endpoint, the LIVE timeline poster, and the RedCompute callbacks.
/// </summary>
public sealed class EventInjector(
    IDiscussions discussions,
    IPluginEvents events,
    RedComputeClient redCompute,
    AgentDirectory agents,
    IEntityStore entities,
    ConversationUnread conversationUnread,
    AgentActivitySignals? activity = null,
    ILogger<EventInjector>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal async Task<LeafEntity?> FindScopedLiveAsync(PluginLiveEventRecipient recipient,CancellationToken ct)
    {
        LeafEntity? found=null;
        // Query the exact durable scope, not a truncated global discussion list.
        // Bound work to four pages and fail closed if uniqueness cannot be established.
        for(var page=0;page<4;page++)
        {
            var rows=await entities.QueryAsync(new EntityQuery {TypeSlug="discussion",Limit=128,Offset=page*128,
                DataEquals=new Dictionary<string,object?>{{"app","nova"},{"type","live"},
                    {"owner_id",recipient.OwnerUserId},{"agent",recipient.AgentId}}},ct);
            if(rows.Count>128)return null;
            foreach(var row in rows)
            {
                var data=row.Data;
                if(data["owner_id"]?.GetValue<string>()!=recipient.OwnerUserId || data["agent"]?.GetValue<string>()!=recipient.AgentId
                    || data["app"]?.GetValue<string>()!="nova" || data["type"]?.GetValue<string>()!="live"
                    || DiscussionStatus.IsClosed(data["status"]?.GetValue<string>()??"stopped"))continue;
                if(found is not null)return null;
                found=row;
            }
            if(rows.Count<128)return found;
        }
        return null;
    }

    /// <summary>Returns true when the event was forwarded into a live session;
    /// false for system events, a failed send, or an event queued until the discussion's
    /// session exists (see <see cref="DeliverPendingAsync"/>). The message itself is persisted
    /// to the discussion stream in every case.</summary>
    public async Task<bool> InjectAsync(
        DiscussionRead discussion, string content, string? type, string? source,
        string? senderAgentId = null, string? replyToDiscussionId = null,
        JsonElement? metadata = null, string? userId = null,
        string? idempotencyKey = null, bool redeliverOnReuse = false,
        CancellationToken ct = default, string? deliveryIdempotencyKey = null, bool framed = false)
        => Forwarded(await InjectCoreAsync(discussion, content, type, source, senderAgentId, replyToDiscussionId,
            metadata, userId, idempotencyKey, redeliverOnReuse, ct, deliveryIdempotencyKey, null, framed: framed));

    /// <summary>
    /// The <c>POST /discussions/{id}/event</c> contract: the detailed outcome, and a RedCompute
    /// admission key on every attempt, so a direct send and a later queue drain of the same event
    /// can never both be admitted. A caller key makes a retry of the whole request idempotent.
    /// </summary>
    public Task<EventInjectionResult> InjectEventAsync(
        DiscussionRead discussion, string content, string? type, string? source,
        string? senderAgentId, string? replyToDiscussionId, JsonElement? metadata, string? userId,
        string? requestIdempotencyKey, CancellationToken ct = default)
    {
        var key = requestIdempotencyKey is null ? null : $"event-request:{requestIdempotencyKey}";
        return InjectCoreAsync(discussion, content, type, source, senderAgentId, replyToDiscussionId,
            metadata, userId, key, redeliverOnReuse: key is not null, ct, key, null, keyByUid: true);
    }

    /// <summary>
    /// Like <see cref="InjectAsync"/>, for unsolicited external notices that must never
    /// revive a closed discussion: the authoritative status is rechecked under the write
    /// gate and an archiving or archived discussion is neither written to nor woken.
    /// Stopped is an open, interrupted state and still receives the notice.
    /// Returns false when the discussion was closed.
    /// </summary>
    public async Task<bool> InjectIfOpenAsync(
        DiscussionRead discussion, string content, string? source,
        JsonElement? metadata, string? userId, string idempotencyKey,
        CancellationToken ct = default, bool framed = false)
        => Forwarded(await InjectCoreAsync(discussion, content, null, source, null, null,
            metadata, userId, idempotencyKey, false, ct, idempotencyKey, null, requireOpen: true, framed: framed));

    // Additive scoped entry point. Legacy injection signature, event identity, persistence,
    // delivery and reconciliation remain unchanged.
    public async Task<bool> InjectScopedLiveAsync(DiscussionRead discussion, PluginLiveEventProjection projection,
        CancellationToken ct = default)
        => Forwarded(await InjectCoreAsync(discussion, projection.Content, "system", projection.Source, null, null,
            projection.Metadata is null ? null : JsonSerializer.SerializeToElement(projection.Metadata),
            projection.Recipient!.OwnerUserId,
            $"scoped-live:{projection.Recipient.OwnerUserId}:{projection.Recipient.AgentId}:{projection.IdempotencyKey}",
            false, ct, null, projection.Recipient));

    // Reuse keeps its historical "already handled" true; queued is not forwarded yet.
    private static bool Forwarded(EventInjectionResult result)
        => result.Delivery is EventDelivery.Delivered or EventDelivery.Reused;

    private async Task<EventInjectionResult> InjectCoreAsync(
        DiscussionRead discussion, string content, string? type, string? source,
        string? senderAgentId, string? replyToDiscussionId, JsonElement? metadata, string? userId,
        string? idempotencyKey, bool redeliverOnReuse, CancellationToken ct,
        string? deliveryIdempotencyKey, PluginLiveEventRecipient? recipient,
        bool requireOpen = false, bool framed = false, bool keyByUid = false)
    {
        var role = type is "assistant" or "system" ? type : "user";
        var sourceTag = $"event:{source ?? "automation"}";

        string? partsJson = null;
        if (metadata is { } meta)
        {
            var parts = new object[]
            {
                new { type = "text", content },
                new { type = "event_data", source, data = meta },
            };
            partsJson = JsonSerializer.Serialize(parts, JsonOptions);
        }

        var uid = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));
        // A queued event is always drained under an admission key; it names the logical event
        // (the caller's delivery key, else the persisted uid), never one attempt.
        PendingEventDelivery QueueEntry(string eventUid) => new(eventUid,
            PendingEventDeliveries.AdmissionKey(deliveryIdempotencyKey ?? "uid:" + eventUid),
            framed, replyToDiscussionId, Clock());
        var admissionDenied = false;
        DiscussionMessage? stored = null;
        QueueDecision? decision = null;
        var persisted = await DiscussionEntityGate.RunAsync(discussion.EntityId, async () =>
        {
            if (requireOpen)
            {
                var current = await entities.GetAsync(discussion.EntityId, ct);
                if (current?.TypeSlug != "discussion"
                    || DiscussionStatus.IsClosed(current.Data["status"] is JsonValue status
                        && status.TryGetValue<string>(out var value)
                            ? value : DiscussionStatus.Stopped))
                { admissionDenied = true; return false; }
            }
            if (recipient is not null)
            {
                // Authoritative scope/lifecycle recheck under the existing write gate.
                // Do not trust a earlier list snapshot across close/ownership changes.
                var current = await entities.GetAsync(discussion.EntityId, ct);
                var data = current?.Data;
                var unique = await FindScopedLiveAsync(recipient,ct);
                if (unique?.Id!=discussion.EntityId || current?.TypeSlug != "discussion" || data?["app"]?.GetValue<string>() != "nova"
                    || data["type"]?.GetValue<string>() != "live"
                    || data["owner_id"]?.GetValue<string>() != recipient.OwnerUserId
                    || data["agent"]?.GetValue<string>() != recipient.AgentId
                    || DiscussionStatus.IsClosed(data["status"]?.GetValue<string>() ?? "stopped")
                    || !LiveEvents.AllowsDisclosure(recipient, data["confidential"]?.GetValue<bool>() == true))
                { admissionDenied = true; return false; }
                discussion = discussion with { Confidential = data["confidential"]?.GetValue<bool>() == true };
            }
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                long afterId = 0;
                while (true)
                {
                    var existing = await discussions.GetMessagesAsync(discussion.EntityId, limit: 1000, afterId: afterId, ct: ct);
                    stored = existing.FirstOrDefault(message => message.Metadata["idempotency_key"] is JsonValue value
                        && value.TryGetValue<string>(out var key)
                        && string.Equals(key, idempotencyKey, StringComparison.Ordinal));
                    if (stored is not null) return false;
                    if (existing.Count < 1000) break;
                    var nextId = existing.Max(message => message.Id);
                    if (nextId <= afterId) throw new InvalidOperationException("Discussion event lookup did not advance");
                    afterId = nextId;
                }
            }
            // Queue before the stream post, in the same gate section: a drain snapshots both under
            // this gate, so every queued uid it sees has its record. A crash in between leaves an
            // entry without a record, which a drain discards; the caller never saw a success.
            if (role != "system")
                decision = await EnqueueIfWaitingAsync(discussion.EntityId, QueueEntry(uid), ct);
            await discussions.PostAsync(discussion.EntityId, role, content, new JsonObject
            {
                ["parts_json"] = partsJson,
                ["source"] = sourceTag,
                ["sender_agent_id"] = senderAgentId,
                ["uid"] = uid,
                ["idempotency_key"] = idempotencyKey,
            }, userId, ct);
            return true;
        }, ct);
        if (admissionDenied) return new(EventDelivery.Denied, null);
        // Most callers only need the durable discussion copy and can treat reuse as
        // success. Heartbeat opts into redelivery because a crash may have happened
        // after persistence but before the session accepted the tick; that path is
        // explicitly classified at-least-once.
        if (!persisted && !redeliverOnReuse) return new(EventDelivery.Reused, StoredUid(stored) ?? uid);
        // The key names one logical event, so what was persisted under it is the event. A
        // redelivery forwards that, never this call's rendering: two callers sharing a key (one
        // turn reported by several registrations) may render different text, and resending the
        // new text under the same admission key would be refused as a conflict on every retry.
        if (stored is not null)
        {
            content = stored.Content;
            uid = StoredUid(stored) ?? uid;
        }

        // Scoped projections always invalidate for authoritative retrieval, even
        // when an owner-approved summary is admitted to a non-confidential LIVE.
        // Do not mirror that summary/evidence link into the public plugin bus.
        if (persisted)
            await PublishEventAsync(discussion, discussion.Confidential || recipient is not null,
                uid, content, source, senderAgentId, metadata, ct);

        if (role == "system") return new(EventDelivery.Recorded, uid);

        // A redelivery decides against the authoritative entity too, never the caller's snapshot.
        decision ??= await DiscussionEntityGate.RunAsync(discussion.EntityId,
            () => EnqueueIfWaitingAsync(discussion.EntityId, QueueEntry(uid), ct), ct);
        if (decision.Current is null)
            return new(EventDelivery.Undeliverable, uid, "discussion_not_found", "The discussion no longer exists.");
        if (decision.Queued)
        {
            // No session yet: the event waits for the one being created, the first user message
            // of a deferred discussion, or the next send after a reset. With a session, it is
            // behind earlier queued events and goes out in order now.
            if (decision.Current.SessionId is null) return new(EventDelivery.Queued, uid);
            await DeliverPendingAsync(discussion.EntityId, ct);
            return await QueuedStateAsync(discussion.EntityId, uid, ct);
        }

        var sessionId = decision.Current.SessionId!;
        string? admissionKey = deliveryIdempotencyKey is not null
            ? PendingEventDeliveries.AdmissionKey(deliveryIdempotencyKey)
            : keyByUid ? PendingEventDeliveries.AdmissionKey("uid:" + uid) : null;
        // Retried callback delivery uses the existing durable admission contract.
        // Other event callers (including heartbeat redelivery) keep their policy.
        var forwarded = await ForwardAsync(discussion with { SessionId = sessionId }, sessionId, content, uid,
            source, senderAgentId, replyToDiscussionId, framed, admissionKey, ct);
        if (forwarded.Success) return new(EventDelivery.Delivered, uid);
        return new(IsPermanentRefusal(forwarded.StatusCode) ? EventDelivery.Undeliverable : EventDelivery.Failed,
            uid, forwarded.ErrorCode ?? "event_delivery_failed", forwarded.ErrorMessage);
    }

    private Task PublishEventAsync(DiscussionRead discussion, bool changedOnly, string uid, string content,
        string? source, string? senderAgentId, JsonElement? metadata, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        return changedOnly
            ? events.PublishAsync("discussion.changed", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["timestamp"] = timestamp,
                ["confidential"] = discussion.Confidential,
            }, ct)
            : events.PublishAsync("discussion.event", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["sessionId"] = discussion.SessionId,
                ["messageUid"] = uid,
                ["content"] = content,
                ["source"] = source ?? "automation",
                ["senderAgentId"] = senderAgentId,
                ["metadata"] = metadata is { } m ? JsonNode.Parse(m.GetRawText()) : null,
                ["timestamp"] = timestamp,
            }, ct);
    }

    private static string? StoredUid(DiscussionMessage? stored)
        => stored?.Metadata["uid"] is JsonValue storedUid && storedUid.TryGetValue<string>(out var value)
            && !string.IsNullOrWhiteSpace(value) ? value : null;

    private async Task<RedComputeClient.SendMessageResult> ForwardAsync(
        DiscussionRead discussion, string sessionId, string content, string uid, string? source,
        string? senderAgentId, string? replyToDiscussionId, bool framed, string? admissionKey,
        CancellationToken ct)
    {
        discussion = await conversationUnread.EnsureBaselineAsync(discussion, ct);

        if (senderAgentId is not null && replyToDiscussionId is not null)
        {
            try
            {
                var callbackUrl = $"http://127.0.0.1:18804/api/apps/nova/callbacks/agent-response?replyTo={replyToDiscussionId}&agentId={discussion.AgentId}";
                await redCompute.RegisterCallbackAsync(sessionId, callbackUrl, force: true, ct);
            }
            catch { }
        }

        try
        {
            // Events reach the session as role "user" (see above), which makes a heartbeat note
            // indistinguishable from the local owner typing. Tag the session copy so Nova can
            // tell an internal note from a person talking.
            // The discussion-stream copy posted above stays untagged, so the UI is unaffected.
            //
            // Only a producer that builds its own frame from escaped values (NovaEventText) passes
            // framed: true. Everything else is untrusted text framed here, with any Nova frame tag
            // inside it made inert, so an agent reply, automation output or posted event cannot
            // close this frame or open a forged one. Content is never trusted for how it starts:
            // text opening with "<nova-event" is framed and neutralized like any other.
            var taggedContent = framed
                ? content
                : $"<nova-event source=\"{NovaEventText.AttributeText(source ?? "automation")}\">\n{NovaEventText.Neutralize(content)}\n</nova-event>";

            // The transcript copy carries the same uid as the stream record —
            // one logical event, one identity.
            object messageBody = senderAgentId is not null
                ? new { content = taggedContent, messageUid = uid, metadata = new { senderAgentId, senderName = await agents.GetAgentNameAsync(senderAgentId, ct) } }
                : new { content = taggedContent, messageUid = uid };
            var agent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ct) : null;
            if (agent == null)
                return new(false, null, 422, "missing_agent", "The discussion has no linked Agent entity.");
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ct);
            var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
                $"/api/apps/nova/discussions/{discussion.Id}/event",
                [new ComputeContextReference("discussion", discussion.Id),
                 new ComputeContextReference("session", sessionId),
                 new ComputeContextReference("event", uid, NameSnapshot: source)], method: "POST", ct: ct);
            var result = await redCompute.SendMessageDetailedAsync(
                sessionId, messageBody, provenance, ct, idempotencyKey: admissionKey);
            // An injected event (heartbeat, automation, callback, delegation, Discord
            // reply) is never a person talking in this discussion.
            if (result.Success) activity?.TurnAdmitted(discussion, userFacing: false);
            return result;
        }
        catch (Exception ex)
        {
            return new(false, null, 500, "event_forward_failed", ex.Message);
        }
    }

    // RedCompute refuses these for the request itself; resending the same event cannot succeed.
    private static bool IsPermanentRefusal(int statusCode) => statusCode is 400 or 413 or 422;

    private sealed record QueueDecision(DiscussionRead? Current, bool Queued);

    private const string QueueNoticeSource = "event-queue";

    /// <summary>Clock for queue ages (tests move it).</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Delay before the scheduled retry that follows a given failed attempt: 15, 30, 60, 120 s.</summary>
    internal Func<int, TimeSpan> RetryBackoff { get; set; } =
        attempt => TimeSpan.FromSeconds(15 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 3)));

    /// <summary>How long a user's send waits for a drain already in progress before it goes ahead.</summary>
    internal TimeSpan UserSendDrainWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Under <see cref="DiscussionEntityGate"/>: queues the event when the discussion has no
    /// session, or when earlier events are still queued (so it cannot overtake them). An entry
    /// already queued under the same uid is not added twice. Expired entries and, past the
    /// discussion's cap, the oldest ones leave the queue and are counted in the discussion.
    /// </summary>
    private async Task<QueueDecision> EnqueueIfWaitingAsync(Guid entityId, PendingEventDelivery entry, CancellationToken ct)
    {
        var entity = await entities.GetAsync(entityId, ct);
        var current = entity is { TypeSlug: "discussion" } ? DiscussionStore.Map(entity) : null;
        if (current is null) return new(null, false);
        var queue = PendingEventDeliveries.Read(entity!.Data);
        if (current.SessionId is not null && queue.Count == 0) return new(current, false);
        if (queue.Any(item => item.Uid == entry.Uid)) return new(current, true);

        var policy = EventQueuePolicy.For(current);
        var expired = Expired(queue, policy);
        queue.RemoveAll(expired.Contains);
        queue.Add(entry);
        // Newest first matters most (a LIVE fills with automation outcomes and whispers): drop the oldest.
        var overflow = queue.Take(Math.Max(0, queue.Count - policy.MaxQueued)).ToList();
        queue.RemoveAll(overflow.Contains);
        var patch = PendingEventDeliveries.Patch(queue);
        await RecordDropsAsync(entity, current, patch, overflow, expired, policy, ct);
        await entities.PatchAsync(entityId, patch, null, ct);
        return new(current, true);
    }

    private List<PendingEventDelivery> Expired(List<PendingEventDelivery> queue, EventQueuePolicy policy)
    {
        var now = Clock();
        return queue.Where(item => item.QueuedAt is { } at && now - at > policy.MaxAge).ToList();
    }

    /// <summary>
    /// Under the gate: counts dropped events into <paramref name="patch"/> and tells the discussion.
    /// Without a session it says once that dropping started; when the session is there it shows the
    /// full count and starts over. Nothing leaves the queue unrecorded.
    /// </summary>
    private async Task RecordDropsAsync(LeafEntity entity, DiscussionRead current, JsonObject patch,
        IReadOnlyCollection<PendingEventDelivery> overflow, IReadOnlyCollection<PendingEventDelivery> expired,
        EventQueuePolicy policy, CancellationToken ct)
    {
        var (overflowed, expiredCount, notified) = PendingEventDeliveries.Drops(entity.Data);
        if (overflow.Count + expired.Count > 0)
        {
            logger?.LogWarning("Dropped {Overflow} queued event(s) over the limit and {Expired} expired one(s) for discussion {DiscussionId}",
                overflow.Count, expired.Count, current.Id);
            patch[PendingEventDeliveries.UndeliverableKey] = PendingEventDeliveries.WithUndeliverable(entity.Data,
                overflow.Select(item => (item.Uid, "queue_overflow")).Concat(expired.Select(item => (item.Uid, "expired"))));
            overflowed += overflow.Count;
            expiredCount += expired.Count;
        }
        var total = overflowed + expiredCount;
        if (total == 0) return;

        if (current.SessionId is not null)
        {
            var parts = new List<string>();
            if (overflowed > 0) parts.Add($"{overflowed} over the limit of {policy.MaxQueued} waiting events");
            if (expiredCount > 0) parts.Add($"{expiredCount} older than {policy.Age}");
            await PostQueueNoticeAsync(current,
                $"{Events(total)} for Nova {(total == 1 ? "was" : "were")} dropped before Nova's session started: {string.Join(", ", parts)}.",
                new { kind = "event-queue-dropped", dropped = total, overflow = overflowed, expired = expiredCount,
                    maxQueued = policy.MaxQueued, maxAgeHours = policy.MaxAge.TotalHours }, ct);
            patch[PendingEventDeliveries.DropsKey] = null;
            return;
        }
        if (!notified)
            await PostQueueNoticeAsync(current,
                $"Nova's session isn't running, so events for Nova wait in a queue of at most {policy.MaxQueued}, for up to {policy.Age}. Older ones are dropped; {total} so far. The full count follows when the session starts.",
                new { kind = "event-queue-dropping", dropped = total, overflow = overflowed, expired = expiredCount,
                    maxQueued = policy.MaxQueued, maxAgeHours = policy.MaxAge.TotalHours }, ct);
        patch[PendingEventDeliveries.DropsKey] = PendingEventDeliveries.DropsNode(overflowed, expiredCount, notified: true);
    }

    private static string Events(int count) => count == 1 ? "1 event" : $"{count} events";

    /// <summary>Under the gate: a display-only notice in the discussion (system role, never queued).</summary>
    private async Task PostQueueNoticeAsync(DiscussionRead discussion, string content, object data, CancellationToken ct)
    {
        var uid = Guid.NewGuid().ToString("N");
        var metadata = JsonSerializer.SerializeToElement(data);
        await discussions.PostAsync(discussion.EntityId, "system", content, new JsonObject
        {
            ["parts_json"] = JsonSerializer.Serialize(new object[]
            {
                new { type = "text", content },
                new { type = "event_data", source = QueueNoticeSource, data = metadata },
            }, JsonOptions),
            ["source"] = $"event:{QueueNoticeSource}",
            ["uid"] = uid,
        }, null, ct);
        await PublishEventAsync(discussion, discussion.Confidential, uid, content, QueueNoticeSource, null, metadata, ct);
    }

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> DeliveryLocks = new();
    private readonly ConcurrentDictionary<Guid, byte> scheduledRetries = new();

    /// <summary>
    /// Delivers a discussion's queued events into its session, oldest first, each under its fixed
    /// admission key, and removes each once RedCompute admitted it. A crash or a concurrent drain
    /// between admission and removal resends under the same key, which RedCompute deduplicates.
    /// A transient failure counts an attempt, schedules a retry and stops the drain so later events
    /// keep their order; the last allowed attempt or a refusal gives the event up, visibly. Without
    /// a session the queue stays. With <paramref name="lockWait"/>, a drain already running for
    /// longer than that is left to finish on its own (<see cref="EventDrainStatus.Busy"/>).
    /// </summary>
    public async Task<EventDrainResult> DeliverPendingAsync(Guid discussionEntityId, CancellationToken ct = default,
        TimeSpan? lockWait = null)
    {
        var drainLock = DeliveryLocks.GetOrAdd(discussionEntityId, _ => new SemaphoreSlim(1, 1));
        if (lockWait is { } wait)
        {
            if (!await drainLock.WaitAsync(wait, ct)) return new(EventDrainStatus.Busy, 0);
        }
        else await drainLock.WaitAsync(ct);
        try
        {
            var delivered = 0;
            while (true)
            {
                var (current, queue, records) = await DiscussionEntityGate.RunAsync(discussionEntityId,
                    () => ReadQueueAsync(discussionEntityId, ct), ct);
                if (current is null || queue.Count == 0) return new(EventDrainStatus.Drained, delivered);
                if (current.SessionId is not { } sessionId) return new(EventDrainStatus.WaitingForSession, delivered);

                foreach (var entry in queue)
                {
                    if (!records.TryGetValue(entry.Uid, out var record))
                    {
                        // Queued, then the process stopped before the stream post: the caller never
                        // saw a success and nothing was shown, so there is nothing to deliver.
                        logger?.LogWarning("Queued event {Uid} for discussion {DiscussionId} has no stream record; discarded",
                            entry.Uid, current.Id);
                        await RemoveAsync(discussionEntityId, entry.Uid, ct);
                        continue;
                    }
                    var source = record.Metadata["source"]?.GetValue<string>() is { } tag && tag.StartsWith("event:", StringComparison.Ordinal)
                        ? tag["event:".Length..] : null;
                    var result = await ForwardAsync(current, sessionId, record.Content, entry.Uid, source,
                        record.Metadata["sender_agent_id"]?.GetValue<string>(), entry.ReplyToDiscussionId,
                        entry.Framed, entry.AdmissionKey, ct);
                    var error = result.ErrorCode ?? $"http_{result.StatusCode}";
                    // A conflict on this event's own key means RedCompute already holds it.
                    if (result.Success || result.ErrorCode == "idempotency_conflict")
                    {
                        await RemoveAsync(discussionEntityId, entry.Uid, ct);
                        delivered++;
                        continue;
                    }
                    if (IsPermanentRefusal(result.StatusCode))
                    {
                        logger?.LogError("RedCompute refused queued event {Uid} for discussion {DiscussionId}: {Error}",
                            entry.Uid, current.Id, error);
                        await FailAttemptAsync(discussionEntityId, entry.Uid, source, error, permanent: true, ct);
                        continue;
                    }
                    logger?.LogWarning("Queued event {Uid} for discussion {DiscussionId} not delivered yet: {Error}",
                        entry.Uid, current.Id, error);
                    var (givenUp, attempts) = await FailAttemptAsync(discussionEntityId, entry.Uid, source, error, permanent: false, ct);
                    if (givenUp) continue;
                    ScheduleRetry(discussionEntityId, attempts);
                    return new(EventDrainStatus.Failed, delivered, error, result.ErrorMessage);
                }
            }
        }
        finally { drainLock.Release(); }
    }

    /// <summary>One in-memory retry per discussion; after a restart the startup sweep takes over.</summary>
    private void ScheduleRetry(Guid discussionEntityId, int attempts)
    {
        if (!scheduledRetries.TryAdd(discussionEntityId, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(RetryBackoff(attempts)); }
            finally { scheduledRetries.TryRemove(discussionEntityId, out _); }
            try { await DeliverPendingAsync(discussionEntityId); }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Retrying queued events failed for discussion entity {EntityId}", discussionEntityId);
            }
        });
    }

    /// <summary>
    /// Drains every discussion that still has queued events and a session, e.g. after a restart
    /// interrupted a drain. Discussions without a session keep their queue for their first send.
    /// </summary>
    public async Task DeliverAllPendingAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3 && !ct.IsCancellationRequested; attempt++)
        {
            var failures = 0;
            IReadOnlyList<LeafEntity> rows;
            try
            {
                rows = await entities.QueryAsync(new EntityQuery
                {
                    TypeSlug = "discussion",
                    DataEquals = new Dictionary<string, object?>
                    {
                        ["app"] = "nova",
                        [PendingEventDeliveries.PendingFlagKey] = true,
                    },
                    Limit = 500,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Could not list discussions with queued events");
                rows = [];
                failures++;
            }
            foreach (var row in rows)
            {
                try
                {
                    if ((await DeliverPendingAsync(row.Id, ct)).Status == EventDrainStatus.Failed) failures++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    logger?.LogWarning(ex, "Queued event delivery failed for discussion entity {EntityId}", row.Id);
                }
            }
            if (failures == 0) return;
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 10), ct);
        }
    }

    /// <summary>Where one event stands after a drain: still queued, given up, or delivered.</summary>
    public async Task<EventInjectionResult> QueuedStateAsync(Guid discussionEntityId, string uid, CancellationToken ct = default)
    {
        var entity = await entities.GetAsync(discussionEntityId, ct);
        if (entity is null)
            return new(EventDelivery.Undeliverable, uid, "discussion_not_found", "The discussion no longer exists.");
        if (PendingEventDeliveries.Read(entity.Data).Any(entry => entry.Uid == uid))
            return new(EventDelivery.Queued, uid);
        return PendingEventDeliveries.UndeliverableError(entity.Data, uid) is { } error
            ? new(EventDelivery.Undeliverable, uid, error, "The event was not delivered; the discussion shows why.")
            : new(EventDelivery.Delivered, uid);
    }

    /// <summary>Under the gate: the queue as it should be delivered now, expired entries removed and
    /// counted, and the drop count shown once the session is there.</summary>
    private async Task<(DiscussionRead? Current, List<PendingEventDelivery> Queue, Dictionary<string, DiscussionMessage> Records)>
        ReadQueueAsync(Guid entityId, CancellationToken ct)
    {
        var records = new Dictionary<string, DiscussionMessage>(StringComparer.Ordinal);
        var entity = await entities.GetAsync(entityId, ct);
        var current = entity is { TypeSlug: "discussion" } ? DiscussionStore.Map(entity) : null;
        if (current is null) return (null, [], records);
        var queue = PendingEventDeliveries.Read(entity!.Data);
        var policy = EventQueuePolicy.For(current);
        var expired = Expired(queue, policy);
        var (overflowed, expiredCount, _) = PendingEventDeliveries.Drops(entity.Data);
        if (expired.Count > 0 || (current.SessionId is not null && overflowed + expiredCount > 0))
        {
            queue.RemoveAll(expired.Contains);
            var patch = PendingEventDeliveries.Patch(queue);
            await RecordDropsAsync(entity, current, patch, [], expired, policy, ct);
            await entities.PatchAsync(entityId, patch, null, ct);
        }
        if (queue.Count == 0) return (current, queue, records);

        var wanted = queue.Select(entry => entry.Uid).ToHashSet(StringComparer.Ordinal);
        long afterId = 0;
        while (records.Count < wanted.Count)
        {
            var page = await discussions.GetMessagesAsync(entityId, limit: 1000, afterId: afterId, ct: ct);
            foreach (var message in page)
                if (message.Metadata["uid"] is JsonValue value
                    && value.TryGetValue<string>(out var messageUid) && wanted.Contains(messageUid))
                    records.TryAdd(messageUid, message);
            if (page.Count < 1000) break;
            var nextId = page.Max(message => message.Id);
            if (nextId <= afterId) throw new InvalidOperationException("Discussion event lookup did not advance");
            afterId = nextId;
        }
        return (current, queue, records);
    }

    private Task RemoveAsync(Guid entityId, string uid, CancellationToken ct)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null) return;
            var queue = PendingEventDeliveries.Read(entity.Data);
            await entities.PatchAsync(entityId,
                PendingEventDeliveries.Patch(queue.Where(entry => entry.Uid != uid).ToList()), null, ct);
        }, ct);

    /// <summary>
    /// Under the gate: counts a failed attempt. A refusal, or the policy's last attempt, gives the
    /// event up: it leaves the queue and the discussion says it was not delivered.
    /// </summary>
    private Task<(bool GivenUp, int Attempts)> FailAttemptAsync(Guid entityId, string uid, string? source,
        string error, bool permanent, CancellationToken ct)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            var current = entity is { TypeSlug: "discussion" } ? DiscussionStore.Map(entity) : null;
            if (current is null) return (true, 0);
            var queue = PendingEventDeliveries.Read(entity!.Data);
            var index = queue.FindIndex(entry => entry.Uid == uid);
            if (index < 0) return (true, 0);
            var attempts = queue[index].Attempts + 1;
            var policy = EventQueuePolicy.For(current);
            if (!permanent && attempts < policy.MaxAttempts)
            {
                queue[index] = queue[index] with { Attempts = attempts };
                await entities.PatchAsync(entityId, PendingEventDeliveries.Patch(queue), null, ct);
                return (false, attempts);
            }

            queue.RemoveAt(index);
            var patch = PendingEventDeliveries.Patch(queue);
            patch[PendingEventDeliveries.UndeliverableKey] = PendingEventDeliveries.WithUndeliverable(entity.Data, [(uid, error)]);
            await entities.PatchAsync(entityId, patch, null, ct);
            var from = string.IsNullOrWhiteSpace(source) ? "an event" : $"an event from {source}";
            await PostQueueNoticeAsync(current, permanent
                    ? $"Nova did not receive {from}: the session refused it ({error}). It was dropped."
                    : $"Nova did not receive {from}: delivery still failed after {attempts} attempts ({error}). It was dropped.",
                new { kind = "event-undeliverable", messageUid = uid, source, error, attempts }, ct);
            return (true, attempts);
        }, ct);
}

/// <summary>
/// Posts system events onto the agent's LIVE discussion timeline (device switches,
/// extension events, delegation callbacks, and discussion activity).
/// </summary>
public sealed class LiveEvents(DiscussionStore store, EventInjector injector, AgentDirectory agents)
{
    private string? _cachedLiveId;
    private string? _lastDeviceName;

    public string? DiscussionId => _cachedLiveId;

    public void InvalidateCache() => _cachedLiveId = null;

    /// <summary>Generic extension routing. Scoped contributions never use legacy fallback.</summary>
    public async Task PostProjectionAsync(PluginLiveEventProjection projection, CancellationToken ct = default)
    {
        if (projection.Recipient is null)
        {
            await PostAsync(projection.Source, projection.Content, projection.Metadata, projection.IdempotencyKey, ct);
            return;
        }
        var recipient = projection.Recipient;
        if (!Guid.TryParse(recipient.OwnerUserId, out var owner) || owner == Guid.Empty
            || !Guid.TryParse(recipient.AgentId, out var agent) || agent == Guid.Empty
            || !Enum.IsDefined(recipient.Disclosure)
            || string.IsNullOrWhiteSpace(projection.IdempotencyKey) || projection.IdempotencyKey.Length > 200
            || string.IsNullOrWhiteSpace(projection.Source) || projection.Source.Length > 80
            || string.IsNullOrWhiteSpace(projection.Content) || projection.Content.Length > 2048
            || projection.Metadata?.ToJsonString().Length > 4096) return;
        // No cache: exact current ownership/lifecycle must be checked every time.
        var entity=await injector.FindScopedLiveAsync(recipient,ct);
        var id=entity?.Data["discussion_id"]?.GetValue<string>();
        if(id is null)return;
        var target=await store.GetAsync(id,ct);
        if(target is null || target.EntityId!=entity!.Id || target.Type!="live" || DiscussionStatus.IsClosed(target.Status)
            || target.OwnerId!=recipient.OwnerUserId || target.AgentId!=recipient.AgentId || !AllowsDisclosure(recipient,target.Confidential))return;
        await injector.InjectScopedLiveAsync(target, projection, ct);
    }

    public static bool AllowsDisclosure(PluginLiveEventRecipient recipient, bool confidential)
        => recipient.Disclosure == PluginLiveDisclosure.ConfidentialOnly ? confidential
            : recipient.Disclosure == PluginLiveDisclosure.OwnerApprovedSummary;

    /// <summary>Posts a LIVE note when the user switches devices mid-conversation.</summary>
    public void NoteDevice(ResolvedDevice device)
    {
        if (device.Name == "unknown" || device.Name == _lastDeviceName) return;
        if (_lastDeviceName != null)
            _ = PostAsync("device", $"Switched to {device.Name}", new { device = device.Name });
        _lastDeviceName = device.Name;
    }

    public async Task PostAsync(
        string source,
        string content,
        object? metadata = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        try
        {
            var live = await ResolveLiveAsync();
            if (live == null) return;

            JsonElement? meta = metadata != null
                ? JsonSerializer.SerializeToElement(metadata)
                : null;
            await injector.InjectAsync(live, content, "system", source,
                metadata: meta, idempotencyKey: idempotencyKey, ct: ct);
        }
        catch { /* the LIVE timeline is best-effort by design */ }
    }

    private async Task<DiscussionRead?> ResolveLiveAsync()
    {
        if (_cachedLiveId != null)
        {
            var cached = await store.GetAsync(_cachedLiveId);
            if (cached is { Type: "live" } && !DiscussionStatus.IsClosed(cached.Status)) return cached;
            _cachedLiveId = null;
        }

        var all = await store.ListByTypeAsync("live", agents.NovaAgentId);
        var live = all.FirstOrDefault(d => !DiscussionStatus.IsClosed(d.Status));
        _cachedLiveId = live?.Id;
        return live;
    }
}

/// <summary>Throttled LIVE-timeline notes about regular discussion activity.</summary>
public sealed class DiscussionActivity(LiveEvents live)
{
    private static readonly Regex XmlTags = new(@"<nova-\w+[\s\S]*?</nova-\w+>\s*", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, DateTime> _lastNovaEvent = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public async Task OnUserMessage(
        string discussionId,
        string? title,
        string contentPreview,
        string? actorName,
        bool confidential = false)
    {
        if (live.DiscussionId == discussionId || string.IsNullOrEmpty(title) || confidential) return;
        var preview = Truncate(contentPreview, 80);
        if (preview == "…") return;
        var actor = NormalizeActorName(actorName);
        await live.PostAsync("discussion", FormatUserActivity(actor, title, preview),
            new { discussionId, title, kind = "user-message", actorName = actor, preview });
    }

    public async Task OnNovaMessage(string discussionId, string? title, string contentPreview, bool confidential = false)
    {
        if (live.DiscussionId == discussionId || string.IsNullOrEmpty(title) || confidential) return;
        if (!TryThrottle(discussionId)) return;
        var preview = Truncate(contentPreview, 80);
        if (preview == "…") return;
        await live.PostAsync("discussion", $"Nova in \"{title}\": {preview}",
            new { discussionId, title, kind = "nova-message", preview });
    }

    public async Task OnArchived(string discussionId, string? title, bool confidential = false)
    {
        if (live.DiscussionId == discussionId || string.IsNullOrEmpty(title) || confidential) return;
        await live.PostAsync("discussion", $"\"{title}\" archived",
            new { discussionId, title, kind = "archived" });
    }

    public async Task OnReopened(string discussionId, string? title, bool confidential = false)
    {
        if (live.DiscussionId == discussionId || string.IsNullOrEmpty(title) || confidential) return;
        await live.PostAsync("discussion", $"\"{title}\" reopened",
            new { discussionId, title, kind = "reopened" });
    }

    private bool TryThrottle(string discussionId)
    {
        var now = DateTime.UtcNow;
        if (_lastNovaEvent.TryGetValue(discussionId, out var last) && now - last < Cooldown)
            return false;
        _lastNovaEvent[discussionId] = now;
        return true;
    }

    private static string Truncate(string s, int max)
    {
        s = XmlTags.Replace(s, "");
        s = s.Replace("\n", " ").Replace("\r", "").Trim();
        if (string.IsNullOrEmpty(s)) return "…";
        return s.Length > max ? s[..max] + "…" : s;
    }

    internal static string NormalizeActorName(string? actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName)) return "User";
        var normalized = Truncate(actorName, 80);
        return normalized == "…" ? "User" : normalized;
    }

    internal static string FormatUserActivity(string actorName, string title, string preview)
        => $"{actorName} in \"{title}\": {preview}";
}
