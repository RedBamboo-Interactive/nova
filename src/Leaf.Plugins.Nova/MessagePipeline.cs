using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed record SendMessageOutcome(
    bool Success, string? SessionId, string? ErrorCode, string? ErrorMessage,
    Dictionary<string, object?>? Metadata = null, string? MessageUid = null);

public sealed class ImageAttachmentDto
{
    public string MediaType { get; set; } = "";
    public string Base64 { get; set; } = "";
}

/// <summary>
/// The shared message-send pipeline: lazily creates the RedCompute session, replays
/// pending injected context, enriches the content with the cross-discussion
/// &lt;nova-context&gt; block, and delivers the message. Used by
/// POST /discussions/{id}/message and POST /ask.
/// </summary>
public sealed class MessagePipeline(
    DiscussionStore store,
    IDiscussions discussions,
    IEntityStore entities,
    RedComputeClient redCompute,
    AgentDirectory agents,
    AgentWorkspaces workspaces,
    NovaConfigStore config,
    LocationService location,
    GeoLocationService geo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, Task<string?>> _pendingSessions = new();

    /// <summary>Kick off background session creation for a fresh discussion.</summary>
    public void BeginSessionCreation(DiscussionRead discussion)
    {
        var discId = discussion.Id;
        _pendingSessions[discId] = Task.Run(async () =>
        {
            try
            {
                var sessionId = await TryCreateSessionAsync(discussion.AgentId, discussion.OwnerId,
                    discussion.QualityTier, discussion.Provider);
                if (sessionId is null)
                {
                    await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Stopped);
                    return null;
                }
                var current = await store.GetAsync(discId);
                if (current is { SessionId: null })
                    await store.PatchAsync(current.EntityId, new JsonObject { ["session_id"] = sessionId });
                return sessionId;
            }
            catch
            {
                try { await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Stopped); }
                catch { /* best effort */ }
                return null;
            }
            finally { _pendingSessions.TryRemove(discId, out _); }
        });
    }

    public async Task<string?> TryCreateSessionAsync(string? agentId, string? ownerId,
        string? qualityTierOverride = null, string? providerOverride = null, CancellationToken ct = default)
    {
        var workspace = await workspaces.GetAsync(agentId, ct);
        workspace.GenerateClaudeMd();

        var appConfig = await config.GetAsync(ct);
        var agentProvider = agentId != null ? await agents.GetAgentProviderAsync(agentId, ct) : null;

        var body = new Dictionary<string, object?>
        {
            ["projectPath"] = workspace.WorkspacePath,
            ["qualityTier"] = qualityTierOverride ?? appConfig.DefaultQualityMode,
        };
        var effectiveProvider = providerOverride ?? agentProvider;
        if (effectiveProvider != null)
            body["provider"] = effectiveProvider;

        return await redCompute.CreateSessionAsync(body, ownerId, "Nova:agent", ct);
    }

    public async Task<SendMessageOutcome> SendAsync(
        DiscussionRead discussion, string? userId,
        string content, ImageAttachmentDto[]? images, ResolvedDevice device, string input,
        double? latitude = null, double? longitude = null, CancellationToken ct = default)
    {
        var sessionId = discussion.SessionId;
        var sessionIsNew = false;

        if (sessionId is null && _pendingSessions.TryRemove(discussion.Id, out var pending))
            sessionId = await pending;

        if (sessionId is null)
        {
            try
            {
                sessionId = await TryCreateSessionAsync(discussion.AgentId, discussion.OwnerId,
                    discussion.QualityTier, discussion.Provider, ct);
            }
            catch
            {
                return new(false, null, "redcompute_unavailable",
                    "RedCompute could not be reached to start a session. Check that it is running on its configured port.");
            }

            if (sessionId is null)
                return new(false, null, "redcompute_unavailable", "RedCompute refused to create a session.");

            sessionIsNew = true;
            await store.PatchAsync(discussion.EntityId, new JsonObject { ["session_id"] = sessionId }, ct: ct);

            // Replay pending assistant messages (from nova-message on pre-created
            // discussions) into the new session so they appear as visible chat bubbles.
            var existing = await discussions.GetMessagesAsync(discussion.EntityId, ct: ct);
            foreach (var msg in existing.Where(m =>
                m.Role == "assistant" && m.Metadata["source"]?.GetValue<string>() == "nova-message"))
            {
                try
                {
                    await redCompute.InjectAsync(sessionId, new
                    {
                        role = "assistant",
                        content = msg.Content,
                        messageUid = msg.Metadata["uid"]?.GetValue<string>(),
                    }, ct);
                }
                catch { /* best-effort — don't block the user's message */ }
            }
        }

        var priorMessage = discussion.InjectedContext;
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-2);

        var all = (await store.ListAsync(ct: ct))
            .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
            .Where(d => !DiscussionStatus.IsClosed(d.Status) || d.LastActivity >= cutoff)
            .Where(d => !d.Confidential || d.Id == discussion.Id)
            .ToList();

        var ownDiscussions = discussion.AgentId != null
            ? all.Where(d => d.AgentId == discussion.AgentId).ToList()
            : all;

        List<DiscussionRead>? otherAgentDiscussions = null;
        if (discussion.AgentId != null)
        {
            otherAgentDiscussions = all
                .Where(d => d.AgentId != discussion.AgentId && !DiscussionStatus.IsClosed(d.Status))
                .Take(5)
                .ToList();
        }

        var agentName = discussion.AgentId != null
            ? await agents.GetAgentNameAsync(discussion.AgentId, ct)
            : null;

        var (currentOutfit, currentOutfitAsset) = await ResolveOutfitContextAsync(discussion.AgentId, ct);

        string? moodSummary = null;
        if (discussion.AgentId != null)
        {
            try
            {
                var workspace = await workspaces.GetAsync(discussion.AgentId, ct);
                var moodPath = Path.Combine(workspace.MemoryPath, "dreaming", "mood.md");
                if (File.Exists(moodPath))
                {
                    var lines = File.ReadAllLines(moodPath);
                    var energy = lines.FirstOrDefault(l => l.StartsWith("Energy:"))?.Trim();
                    var vibe = lines.FirstOrDefault(l => l.StartsWith("Vibe:"))?.Trim();
                    if (energy != null || vibe != null)
                        moodSummary = string.Join(", ", new[] { energy, vibe }.Where(s => s != null));
                }
            }
            catch { }
        }

        if (latitude.HasValue && longitude.HasValue)
            location.UpdateLocation(latitude.Value, longitude.Value, null);
        var locReading = location.Latest;

        List<ContextSnapshot.LiveEventEntry>? liveEvents = null;
        var liveDiscussion = ownDiscussions.FirstOrDefault(d => d.Type == "live");
        if (liveDiscussion != null)
        {
            var liveMessages = await discussions.GetMessagesAsync(liveDiscussion.EntityId, ct: ct);
            liveEvents = liveMessages
                .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
                .TakeLast(15)
                .Select(m => new ContextSnapshot.LiveEventEntry
                {
                    Source = m.Metadata["source"]?.GetValue<string>() ?? "",
                    Content = m.Content,
                    Timestamp = ParseTimestamp(m) ?? m.CreatedAt.UtcDateTime,
                })
                .ToList();
        }

        var currentSnapshot = NovaContextBuilder.BuildSnapshot(
            ownDiscussions, otherAgentDiscussions, currentOutfit, currentOutfitAsset,
            geo.Location, moodSummary, latitude, longitude,
            locReading?.Zone, locReading?.PlaceName, locReading?.Timezone, liveEvents);
        var previousSnapshot = NovaContextBuilder.DeserializeSnapshot(discussion.LastContextJson);

        var reactionLines = await GetRecentReactionLinesAsync(
            discussion, discussion.LastContextJson != null ? discussion.LastActivity : DateTime.MinValue, ct);

        string contextBlock;
        if (previousSnapshot == null || discussion.MessageCount == 0)
            contextBlock = NovaContextBuilder.BuildFullContext(currentSnapshot, discussion.Id, now, device, input, agentName, reactionLines);
        else
            contextBlock = NovaContextBuilder.BuildDeltaContext(currentSnapshot, previousSnapshot, discussion.Id, now, device, input, agentName, reactionLines);

        var metadata = NovaContextBuilder.BuildMetadata(currentSnapshot, now, device, input, discussion.Id, agentName);

        var priorBlock = priorMessage != null
            ? $"\n<nova-prior-message role=\"assistant\">\n{priorMessage}\n</nova-prior-message>\n"
            : "";
        var enrichedContent = contextBlock + priorBlock + "\n" + content;

        string? messageUid = null;
        var sendResult = await redCompute.SendMessageAsync(sessionId, new { content = enrichedContent, images }, ct);
        if (sendResult is null)
        {
            return new(false, sessionId, "redcompute_unavailable",
                "RedCompute could not deliver the message to the session.");
        }
        // RedCompute mints the message uid at ingestion; carry it so Nova's copy of this
        // message and the frontend's optimistic block share the transcript record's identity.
        if (sendResult.Value.ValueKind == JsonValueKind.Object
            && sendResult.Value.TryGetProperty("messageUid", out var muEl))
            messageUid = muEl.GetString();

        var hasImages = images is { Length: > 0 };
        var patch = new JsonObject
        {
            ["last_activity"] = new DateTimeOffset(now).ToString("O"),
            ["last_context_json"] = NovaContextBuilder.SerializeSnapshot(currentSnapshot),
        };
        // One count bump per user message: the image path gets it from PostAsync below.
        if (!hasImages)
            patch["message_count"] = discussion.MessageCount + 1;
        if (discussion.InjectedContext != null)
            patch["injected_context"] = null;
        if (sessionIsNew)
            patch["session_id"] = sessionId;
        await store.PatchAsync(discussion.EntityId, patch, ct: ct);

        if (hasImages)
        {
            // Persisted with the transcript uid — one logical message, one identity.
            await store.PostMessageAsync(discussion.EntityId, "user", content, new JsonObject
            {
                ["parts_json"] = JsonSerializer.Serialize(
                    images!.Select(img => new { type = "image", mediaType = img.MediaType, base64 = img.Base64 }),
                    JsonOptions),
                ["source"] = "user-message",
                ["uid"] = messageUid ?? Guid.NewGuid().ToString("N"),
            }, userId, ct);
        }

        return new(true, sessionId, null, null, metadata, messageUid);
    }

    public async Task<(string? Outfit, string? Asset)> ResolveOutfitContextAsync(string? agentId, CancellationToken ct = default)
    {
        if (agentId == null || !Guid.TryParse(agentId, out var agentGuid)) return (null, null);
        try
        {
            var agent = await entities.GetAsync(agentGuid, ct);
            if (agent == null) return (null, null);

            var outfitRef = agent.Data["outfit"]?.GetValue<string>();
            if (string.IsNullOrEmpty(outfitRef) || outfitRef.StartsWith('/') || !Guid.TryParse(outfitRef, out var outfitGuid))
                return (null, null);

            var outfit = await entities.GetAsync(outfitGuid, ct);
            if (outfit == null) return (null, null);

            var prompt = outfit.Data["prompt"]?.GetValue<string>();
            var asset = outfit.Data["asset"]?.GetValue<string>();
            var reasoning = outfit.Data["reasoning"]?.GetValue<string>();

            var text = $"You're wearing \"{outfit.Name}\" today ({prompt}).";
            if (reasoning != null) text += $" You chose it because: {reasoning}";
            string? assetUrl = null;
            if (asset != null)
            {
                assetUrl = asset.Contains("://") ? asset : $"http://127.0.0.1:18804{(asset.StartsWith('/') ? asset : "/api/assets/" + asset)}";
                text += $"\nSee it: {assetUrl}";
            }
            return (text, assetUrl);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<List<string>> GetRecentReactionLinesAsync(DiscussionRead discussion, DateTime since, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            var sinceOffset = since == DateTime.MinValue
                ? (DateTimeOffset?)null
                : new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc));
            var records = await discussions.GetReactionsAsync(discussion.EntityId, sinceOffset, 50, ct);
            if (records.Count == 0) return lines;

            var messages = await discussions.GetMessagesAsync(discussion.EntityId, ct: ct);
            var msgByUid = new Dictionary<string, string>();
            foreach (var m in messages)
            {
                var uid = m.Metadata["uid"]?.GetValue<string>();
                if (uid != null && !msgByUid.ContainsKey(uid))
                {
                    var preview = m.Content.Replace("\n", " ");
                    if (preview.Length > 60) preview = preview[..57] + "...";
                    msgByUid[uid] = preview;
                }
            }

            foreach (var rec in records)
            {
                var d = rec.Data;
                if ((d["action"]?.GetValue<string>() ?? "add") != "add") continue;

                var emoji = d["emoji"]?.GetValue<string>() ?? "";
                var actorName = d["actor_name"]?.GetValue<string>() ?? "";
                var msgKey = d["message_key"]?.GetValue<string>() ?? "";

                var preview = msgByUid.GetValueOrDefault(msgKey);
                lines.Add(preview != null
                    ? $"{actorName} reacted {emoji} to: \"{preview}\""
                    : $"{actorName} reacted {emoji}");
            }
        }
        catch { }
        return lines;
    }

    private static DateTime? ParseTimestamp(DiscussionMessage m)
    {
        var ts = m.Metadata["timestamp"]?.GetValue<string>();
        return ts != null && DateTimeOffset.TryParse(ts, out var t) ? t.UtcDateTime : null;
    }
}
