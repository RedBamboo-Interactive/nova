using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Shared event-injection pipeline: persists the event as a discussion message,
/// broadcasts it to the UI (via the plugin-event → WebSocket bridge), and forwards
/// non-system events into the live RedCompute session. Used by the /event endpoint,
/// the LIVE timeline poster, and the RedCompute callbacks.
/// </summary>
public sealed class EventInjector(
    IDiscussions discussions,
    IPluginEvents events,
    RedComputeClient redCompute,
    AgentDirectory agents,
    IEntityStore entities)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Returns true when the event was forwarded into a live session;
    /// false for system events, session-less discussions, or a failed send. The
    /// message itself is persisted to the discussion stream in every case.</summary>
    public async Task<bool> InjectAsync(
        DiscussionRead discussion, string content, string? type, string? source,
        string? senderAgentId = null, string? replyToDiscussionId = null,
        JsonElement? metadata = null, string? userId = null,
        string? idempotencyKey = null, bool redeliverOnReuse = false,
        CancellationToken ct = default)
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
        var persisted = await DiscussionEntityGate.RunAsync(discussion.EntityId, async () =>
        {
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await discussions.GetMessagesAsync(discussion.EntityId, ct: ct);
                if (existing.Any(message => message.Metadata["idempotency_key"] is JsonValue value
                    && value.TryGetValue<string>(out var key)
                    && string.Equals(key, idempotencyKey, StringComparison.Ordinal)))
                    return false;
            }
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
        // Most callers only need the durable discussion copy and can treat reuse as
        // success. Heartbeat opts into redelivery because a crash may have happened
        // after persistence but before the session accepted the tick; that path is
        // explicitly classified at-least-once.
        if (!persisted && !redeliverOnReuse) return true;

        if (persisted)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            await events.PublishAsync("discussion.event", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["sessionId"] = discussion.SessionId,
                ["content"] = content,
                ["source"] = source ?? "automation",
                ["senderAgentId"] = senderAgentId,
                ["metadata"] = metadata is { } m ? JsonNode.Parse(m.GetRawText()) : null,
                ["timestamp"] = timestamp,
            }, ct);
        }

        if (discussion.SessionId is null || role == "system") return false;

        if (senderAgentId is not null && replyToDiscussionId is not null)
        {
            try
            {
                var callbackUrl = $"http://127.0.0.1:18804/api/apps/nova/callbacks/agent-response?replyTo={replyToDiscussionId}&agentId={discussion.AgentId}";
                await redCompute.RegisterCallbackAsync(discussion.SessionId, callbackUrl, force: true, ct);
            }
            catch { }
        }

        try
        {
            // Events reach the session as role "user" (see above), which makes a heartbeat note
            // indistinguishable from Laurent typing. Nova then answers it in the discussion he
            // reads. Tag the session copy so she can tell an internal note from him talking.
            // The discussion-stream copy posted above stays untagged, so the UI is unaffected.
            var taggedContent = content.TrimStart().StartsWith("<nova-event", StringComparison.Ordinal)
                ? content
                : $"<nova-event source=\"{source ?? "automation"}\">\n{content}\n</nova-event>";

            // The transcript copy carries the same uid as the stream record —
            // one logical event, one identity.
            object messageBody = senderAgentId is not null
                ? new { content = taggedContent, messageUid = uid, metadata = new { senderAgentId, senderName = await agents.GetAgentNameAsync(senderAgentId, ct) } }
                : new { content = taggedContent, messageUid = uid };
            var agent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ct) : null;
            if (agent == null) return false;
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ct);
            var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
                $"/api/apps/nova/discussions/{discussion.Id}/event",
                [new ComputeContextReference("discussion", discussion.Id),
                 new ComputeContextReference("session", discussion.SessionId),
                 new ComputeContextReference("event", uid, NameSnapshot: source)], method: "POST", ct: ct);
            return await redCompute.SendMessageAsync(discussion.SessionId, messageBody, ct, provenance) != null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Posts system events onto the agent's LIVE discussion timeline (device switches,
/// location changes, delegation callbacks, discussion activity).
/// </summary>
public sealed class LiveEvents(DiscussionStore store, EventInjector injector, AgentDirectory agents)
{
    private string? _cachedLiveId;
    private string? _lastDeviceName;

    public string? DiscussionId => _cachedLiveId;

    public void InvalidateCache() => _cachedLiveId = null;

    /// <summary>Posts a LIVE note when the user switches devices mid-conversation.</summary>
    public void NoteDevice(ResolvedDevice device)
    {
        if (device.Name == "unknown" || device.Name == _lastDeviceName) return;
        if (_lastDeviceName != null)
            _ = PostAsync("device", $"Switched to {device.Name}", new { device = device.Name });
        _lastDeviceName = device.Name;
    }

    public async Task PostAsync(string source, string content, object? metadata = null)
    {
        try
        {
            var live = await ResolveLiveAsync();
            if (live == null) return;

            JsonElement? meta = metadata != null
                ? JsonSerializer.SerializeToElement(metadata)
                : null;
            await injector.InjectAsync(live, content, "system", source, metadata: meta);
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

        var all = await store.ListAsync(agents.NovaAgentId);
        var live = all.FirstOrDefault(d => d.Type == "live" && !DiscussionStatus.IsClosed(d.Status)
            && (agents.NovaAgentId == null || d.AgentId == agents.NovaAgentId));
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

    public async Task OnUserMessage(string discussionId, string? title, string contentPreview, bool confidential = false)
    {
        if (live.DiscussionId == discussionId || string.IsNullOrEmpty(title) || confidential) return;
        var preview = Truncate(contentPreview, 80);
        if (preview == "…") return;
        await live.PostAsync("discussion", $"Laurent in \"{title}\": {preview}",
            new { discussionId, title, kind = "user-message", preview });
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
}
