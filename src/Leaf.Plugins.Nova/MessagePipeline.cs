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

public sealed class InputPartDto
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? AttachmentId { get; set; }
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
    IAssets assets,
    RedComputeClient redCompute,
    AgentDirectory agents,
    AgentWorkspaces workspaces,
    NovaConfigStore config,
    ExtensionContributions extensions)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, Task<string?>> _pendingSessions = new();

    /// <summary>Kick off background session creation for a fresh discussion.</summary>
    public void BeginSessionCreation(DiscussionRead discussion)
    {
        if (discussion.SessionId is not null) return;
        var discId = discussion.Id;
        _pendingSessions.GetOrAdd(discId, _discussionKey => Task.Run(async () =>
        {
            try
            {
                var sessionId = await TryCreateSessionAsync(discussion.AgentId, discussion.OwnerId,
                    discussion.QualityTier, discussion.Provider,
                    discussionId: discussion.Id);
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
        }));
    }

    public async Task<string?> TryCreateSessionAsync(string? agentId, string? ownerId,
        string? qualityTierOverride = null, string? providerOverride = null, CancellationToken ct = default,
        string? discussionId = null, string entrypointRoute = "/api/apps/nova/discussions/{id}/messages",
        IReadOnlyList<ComputeContextReference>? additionalContext = null,
        string? correlationId = null, string? parentJobId = null)
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

        var agent = agentId != null ? await agents.GetAgentAsync(agentId, ct) : null;
        if (agent == null) return null;
        var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, ownerId, ct);
        var context = new List<ComputeContextReference>();
        if (discussionId != null) context.Add(new ComputeContextReference("discussion", discussionId));
        if (additionalContext != null) context.AddRange(additionalContext);
        var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
            entrypointRoute, context, correlationId: correlationId, parentJobId: parentJobId, ct: ct);
        return await redCompute.CreateSessionAsync(body, ownerId, "Nova:agent", provenance, ct);
    }

    public Task<SendMessageOutcome> SendAsync(
        DiscussionRead discussion, string? userId,
        string content, ImageAttachmentDto[]? images, ResolvedDevice device, string input,
        CancellationToken ct = default)
        => SendCoreAsync(discussion, userId, content, images, null, device, input, ct);

    public Task<SendMessageOutcome> SendInputAsync(
        DiscussionRead discussion, string? userId,
        InputPartDto[] parts, ResolvedDevice device, string input, CancellationToken ct = default)
    {
        var content = string.Join("\n", parts
            .Where(part => string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(part => part.Text ?? ""));
        return SendCoreAsync(discussion, userId, content, null, parts, device, input, ct);
    }

    private async Task<SendMessageOutcome> SendCoreAsync(
        DiscussionRead discussion, string? userId,
        string content, ImageAttachmentDto[]? images, InputPartDto[]? inputParts,
        ResolvedDevice device, string input, CancellationToken ct = default)
    {
        var sessionId = discussion.SessionId;
        var sessionIsNew = false;
        var computeOwnerId = discussion.OwnerId;

        if (sessionId is null && _pendingSessions.TryRemove(discussion.Id, out var pending))
            sessionId = await pending;

        if (sessionId is null)
        {
            computeOwnerId = DiscussionOwnership.ResolveForSessionStart(
                discussion.OwnerId, userId, needsSession: true);
            try
            {
                sessionId = await TryCreateSessionAsync(discussion.AgentId, computeOwnerId,
                    discussion.QualityTier, discussion.Provider, ct, discussion.Id);
            }
            catch
            {
                return new(false, null, "redcompute_unavailable",
                    "RedCompute could not be reached to start a session. Check that it is running on its configured port.");
            }

            if (sessionId is null)
                return new(false, null, "redcompute_unavailable", "RedCompute refused to create a session.");

            sessionIsNew = true;
            var sessionPatch = new JsonObject { ["session_id"] = sessionId };
            if (!string.Equals(computeOwnerId, discussion.OwnerId, StringComparison.Ordinal))
                sessionPatch["owner_id"] = computeOwnerId;
            await store.PatchAsync(discussion.EntityId, sessionPatch, ct: ct);

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

        var extensionContexts = await extensions.CollectContextAsync(
            userId, discussion.AgentId, discussion.Id, "conversation", ct);

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
            mood: moodSummary,
            liveEvents: liveEvents,
            extensionContexts: extensionContexts);
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
        object requestBody;
        if (inputParts is { Length: > 0 })
        {
            var typedInput = new List<object>();
            var enriched = false;
            foreach (var part in inputParts)
            {
                if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    typedInput.Add(new { type = "text", text = enriched
                        ? part.Text ?? ""
                        : contextBlock + priorBlock + "\n" + (part.Text ?? "") });
                    enriched = true;
                }
                else if (string.Equals(part.Type, "attachment", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(part.AttachmentId))
                {
                    typedInput.Add(new { type = "attachment", attachmentId = part.AttachmentId });
                }
            }
            if (!enriched)
                typedInput.Insert(0, new { type = "text", text = contextBlock + priorBlock });
            requestBody = new { input = typedInput };
        }
        else
        {
            requestBody = new { content = enrichedContent, images };
        }

        var agent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ct) : null;
        if (agent == null)
            return new(false, sessionId, "missing_agent", "The discussion has no linked Agent entity.");
        var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, computeOwnerId, ct);
        var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
            $"/api/apps/nova/discussions/{discussion.Id}/message",
            [new ComputeContextReference("discussion", discussion.Id),
             new ComputeContextReference("session", sessionId)], method: "POST", ct: ct);
        var sendResult = await redCompute.SendMessageDetailedAsync(sessionId, requestBody, ct, provenance);
        if (!sendResult.Success)
        {
            return new(false, sessionId,
                sendResult.ErrorCode ?? "redcompute_unavailable",
                sendResult.ErrorMessage ?? "RedCompute could not deliver the message to the session.");
        }
        // RedCompute mints the message uid at ingestion; carry it so Nova's copy of this
        // message and the frontend's optimistic block share the transcript record's identity.
        if (sendResult.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("messageUid", out var muEl))
            messageUid = muEl.GetString();

        JsonElement[] claimedAttachments = [];
        if (sendResult.Payload is { ValueKind: JsonValueKind.Object } attachmentPayload
            && attachmentPayload.TryGetProperty("attachments", out var attachmentArray)
            && attachmentArray.ValueKind == JsonValueKind.Array)
            claimedAttachments = attachmentArray.EnumerateArray().Select(item => item.Clone()).ToArray();

        var hasImages = images is { Length: > 0 };
        var hasAttachments = claimedAttachments.Length > 0;
        var acceptedMessageUid = messageUid ?? Guid.NewGuid().ToString("N");
        var patch = new JsonObject
        {
            ["last_activity"] = new DateTimeOffset(now).ToString("O"),
            ["last_context_json"] = NovaContextBuilder.SerializeSnapshot(currentSnapshot),
        };
        if (discussion.InjectedContext != null)
            patch["injected_context"] = null;
        if (sessionIsNew)
            patch["session_id"] = sessionId;
        await store.PatchAsync(discussion.EntityId, patch, ct: ct);

        if (hasAttachments)
        {
            var parts = claimedAttachments.Select(attachment => new
            {
                type = "attachment",
                id = attachment.GetProperty("id").GetString(),
                kind = attachment.GetProperty("kind").GetString(),
                name = attachment.GetProperty("name").GetString(),
                mediaType = attachment.GetProperty("mediaType").GetString(),
                size = attachment.GetProperty("size").GetInt64(),
                sha256 = attachment.TryGetProperty("sha256", out var hash) ? hash.GetString() : null,
                downloadUrl = attachment.GetProperty("downloadUrl").GetString(),
            }).ToArray();

            await store.PostMessageAsync(discussion.EntityId, "user", content, new JsonObject
            {
                ["parts_json"] = JsonSerializer.Serialize(parts, JsonOptions),
                ["source"] = "user-message",
                ["uid"] = acceptedMessageUid,
            }, userId, ct);
        }
        else if (hasImages)
        {
            var parts = new List<object>();
            foreach (var img in images!)
            {
                var bytes = Convert.FromBase64String(img.Base64);
                using var ms = new MemoryStream(bytes);
                var asset = await assets.UploadAsync(ms, "image", img.MediaType, ct);
                parts.Add(new { type = "image", assetId = asset.AssetId, url = asset.Url, mediaType = img.MediaType });
            }

            await store.PostMessageAsync(discussion.EntityId, "user", content, new JsonObject
            {
                ["parts_json"] = JsonSerializer.Serialize(parts, JsonOptions),
                ["source"] = "user-message",
                ["uid"] = acceptedMessageUid,
            }, userId, ct);
        }
        else
        {
            // Persist every accepted user message before the endpoint publishes its
            // convergence event. RedCompute mirrors the message asynchronously, so
            // this record is Nova's authoritative bridge during that short window.
            // PostMessageAsync owns the single message_count increment for all paths.
            await store.PostMessageAsync(discussion.EntityId, "user", content, new JsonObject
            {
                ["source"] = "user-message",
                ["uid"] = acceptedMessageUid,
            }, userId, ct);
        }

        return new(true, sessionId, null, null, metadata, acceptedMessageUid);
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
