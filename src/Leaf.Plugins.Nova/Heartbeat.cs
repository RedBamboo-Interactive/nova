using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Per-agent heartbeat behavior plus a reference to its canonical automation.
/// Schedule and enabled state belong to that automation; legacy copies are read
/// only long enough for reconciliation to migrate them.
/// </summary>
public sealed record HeartbeatConfig(
    Guid? AutomationId,
    bool? LegacyEnabled,
    string? LegacySchedule,
    string QualityTier,
    int TickWaitMinutes,
    int MaxTurns)
{
    // :55 keeps consecutive ticks inside the 1h prompt-cache TTL. Hour 2 is not a
    // regular tick: the handler turns the 02:55 firing into the end-of-day fallback
    // when no goodnight/rotation signal closed the day.
    public const string DefaultSchedule = "55 2,6-23 * * *";

    public static HeartbeatConfig? Parse(JsonObject agentData)
    {
        if (agentData["heartbeat"] is not JsonObject hb) return null;
        return new HeartbeatConfig(
            AutomationId: Guid.TryParse(Str(hb, "automation_id"), out var automationId)
                ? automationId : null,
            LegacyEnabled: hb["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b)
                ? b : null,
            LegacySchedule: Str(hb, "schedule"),
            QualityTier: Str(hb, "quality_tier") ?? "deep",
            TickWaitMinutes: Int(hb, "tick_wait_minutes") ?? 15,
            MaxTurns: Int(hb, "max_turns") ?? 15);
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}

/// <summary>
/// The heartbeat: a per-agent background participant. One standing discussion of
/// type <c>heartbeat</c> (LIVE-rendered, no chat input) with a persistent session
/// behind it, ticked by a <c>system:heartbeat:{agent}</c> automation. This service
/// owns provisioning (agent config → automation + discussion), the day boundary
/// (end-of-day tick → handoff → session reset), and the per-tick state kept on the
/// heartbeat discussion entity (<c>hb_last_tick_at</c>).
/// </summary>
public sealed class HeartbeatService(
    IEntityStore entities,
    IWorkflowAutomations workflowAutomations,
    IDiscussions discussions,
    DiscussionStore store,
    DiscussionLifecycle lifecycle,
    MessagePipeline pipeline,
    RedComputeClient redCompute,
    EventInjector injector,
    AgentDirectory agents,
    LocationService location)
{
    public const string DiscussionType = "heartbeat";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rotationGates = new();

    private static string AutomationSlug(string agentSlug) => $"system-heartbeat-{agentSlug}";

    // ── Provisioning ────────────────────────────────────────────────

    /// <summary>
    /// Link every agent heartbeat to one canonical automation. Legacy schedule and
    /// enabled copies are consumed once, then removed from the agent config. Later
    /// reconciliations never overwrite the automation definition.
    /// </summary>
    public async Task<JsonObject> ReconcileAsync(CancellationToken ct = default)
    {
        var result = new JsonObject { ["provisioned"] = new JsonArray(), ["tornDown"] = new JsonArray() };
        var agentEntities = await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 50 }, ct);
        var users = await entities.QueryAsync(new EntityQuery { TypeSlug = "user", Limit = 2 }, ct);
        var soleOwnerId = users.Count == 1 ? users[0].Id.ToString() : null;

        foreach (var agent in agentEntities)
        {
            var agentId = agent.Id.ToString();

            // ── LIVE discussion: enabled by agent data.live == true ──
            var liveEnabled = agent.Data["live"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb;
            var existingLive = (await store.ListAsync(agentId, ct))
                .FirstOrDefault(d => d.Type == "live" && !DiscussionStatus.IsClosed(d.Status));

            if (liveEnabled && existingLive == null)
                await store.CreateAsync($"{agent.Name} Live", agentId, soleOwnerId ?? "system", "live", ct: ct);

            // ── Heartbeat: enabled by agent data.heartbeat.enabled ──
            var config = HeartbeatConfig.Parse(agent.Data);
            var automation = config?.AutomationId is { } automationId
                ? await entities.GetAsync(automationId, ct)
                : await entities.GetBySlugAsync(AutomationSlug(agent.Slug), ct);
            if (automation is not null && (automation.TypeSlug != "automation"
                || StrValue(automation.Data, "agent") != agentId))
                automation = await entities.GetBySlugAsync(AutomationSlug(agent.Slug), ct);
            var discussion = await GetDiscussionAsync(agentId, ct);

            if (config != null)
            {
                if (automation == null)
                {
                    var legacyEnabled = config.LegacyEnabled ?? false;
                    var schedule = config.LegacySchedule ?? HeartbeatConfig.DefaultSchedule;
                    automation = await workflowAutomations.EnsureAsync(
                        CreateAutomationDefinition(agent.Slug, agentId, agent.Name,
                            schedule, legacyEnabled, config, soleOwnerId), ct);
                }

                if (config.AutomationId != automation.Id
                    || config.LegacyEnabled is not null || config.LegacySchedule is not null)
                {
                    var heartbeatData = agent.Data["heartbeat"]?.DeepClone() as JsonObject
                        ?? new JsonObject();
                    heartbeatData.Remove("enabled");
                    heartbeatData.Remove("schedule");
                    heartbeatData["automation_id"] = automation.Id.ToString();
                    await entities.PatchAsync(agent.Id, new JsonObject
                    {
                        ["heartbeat"] = heartbeatData,
                    }, ct: ct);
                }

                var enabled = IsEnabled(automation.Data);
                if (enabled)
                {
                    discussion ??= await store.CreateAsync($"{agent.Name} Heartbeat", agentId,
                        soleOwnerId ?? "system", DiscussionType, config.QualityTier, ct: ct);
                    ((JsonArray)result["provisioned"]!).Add(new JsonObject
                    {
                        ["agent"] = agent.Slug,
                        ["automationId"] = automation.Id.ToString(),
                        ["discussionId"] = discussion.Id,
                    });
                }
                else if (discussion != null)
                {
                    await lifecycle.BeginArchiveAsync(discussion, ct);
                    ((JsonArray)result["tornDown"]!).Add(agent.Slug);
                }
            }
            else
            {
                if (automation != null)
                    await ArchiveAutomationAsync(automation, ct);
                if (discussion != null)
                    await lifecycle.BeginArchiveAsync(discussion, ct);
                if (automation != null || discussion != null)
                    ((JsonArray)result["tornDown"]!).Add(agent.Slug);
            }
        }
        return result;
    }

    private static WorkflowAutomationDefinition CreateAutomationDefinition(
        string agentSlug, string agentId, string agentName,
        string schedule, bool enabled, HeartbeatConfig config, string? ownerId)
    {
        var actionConfig = new JsonObject();
        var beneficiary = ownerId is null
            ? new JsonObject
            {
                ["kind"] = "unreviewed",
                ["reason"] = $"Heartbeat for {agentName} has no explicitly authored beneficiary",
                ["authored"] = false,
            }
            : new JsonObject
            {
                ["kind"] = "user",
                ["id"] = ownerId,
                ["authored"] = true,
                ["authored_by"] = "system:heartbeat-provisioning",
                ["authored_at"] = DateTimeOffset.UtcNow,
            };
        var ownership = new JsonObject
        {
            ["app"] = "nova",
            ["actor_agent"] = agentId,
            ["beneficiary"] = beneficiary,
        };
        if (ownerId is not null)
            ownership["user_id"] = ownerId;
        return new WorkflowAutomationDefinition
        {
            Slug = AutomationSlug(agentSlug),
            Name = $"system:heartbeat:{agentSlug}",
            NodeType = HeartbeatTickHandler.Type,
            Enabled = enabled,
            Trigger = new JsonObject
            {
                ["kind"] = "cron",
                ["expression"] = schedule,
                ["timezone"] = "Europe/Zurich",
                ["misfire_policy"] = "skip",
                ["grace_seconds"] = 120,
                ["migration_policy"] = "canonical",
                ["migration_reason"] = "Heartbeat schedule is authored as local wall-clock time",
            },
            ActionConfig = actionConfig,
            NodeContext = new JsonObject { ["agent"] = agentId },
            ExecutionPolicy = new JsonObject
            {
                ["overlap"] = "forbid",
                ["timeout_seconds"] = (config.TickWaitMinutes + 5) * 60,
                ["lease_seconds"] = 300,
                ["max_failures"] = 20,
                ["retry_count"] = 0,
                ["retry_delay_seconds"] = 30,
                ["recovery"] = "at-least-once",
                ["recovery_reason"] = "A crash between session delivery and its durable marker can redeliver one tick",
            },
            Ownership = ownership,
            Metadata = new JsonObject
            {
                ["agent"] = agentId,
                ["owner_id"] = ownerId,
                ["timeout"] = (config.TickWaitMinutes + 5) * 60,
            },
            Description = $"Runs the recurring heartbeat for {agentName}.",
            ReviewReason = "Nova heartbeat workflow authored by the installed plugin",
        };
    }

    private async Task ArchiveAutomationAsync(LeafEntity automation, CancellationToken ct)
    {
        if (!IsEnabled(automation.Data)
            && automation.Data["archived_reason"]?.GetValue<string>() == "heartbeat-reference-removed")
            return;
        await entities.PatchAsync(automation.Id, new JsonObject
        {
            ["enabled"] = false,
            ["archived_at"] = DateTimeOffset.UtcNow,
            ["archived_reason"] = "heartbeat-reference-removed",
        }, ct: ct);
    }

    private static bool IsEnabled(JsonObject data)
        => data["enabled"] is JsonValue enabled
            && enabled.TryGetValue<bool>(out var value) && value;

    private static string? StrValue(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result : null;

    public async Task<DiscussionRead?> GetDiscussionAsync(string agentId, CancellationToken ct = default)
    {
        var all = await store.ListAsync(agentId, ct);
        return all.FirstOrDefault(d =>
            d.Type == DiscussionType && d.AgentId == agentId && !DiscussionStatus.IsClosed(d.Status));
    }

    // ── Day boundary ────────────────────────────────────────────────

    /// <summary>LIVE rotation is the day boundary; fire-and-forget from the rotate flow.</summary>
    public void OnLiveRotated(string? agentId, string? idempotencyKey = null)
    {
        if (agentId == null) return;
        _ = Task.Run(async () =>
        {
            try { await RotateAsync(agentId, "live-rotation",
                idempotencyKey: idempotencyKey); }
            catch { /* fallback tick at 02:55 closes the day if this failed */ }
        });
    }

    /// <summary>
    /// End-of-day / reset: archive the current heartbeat discussion (with its
    /// session) and create a fresh one. Same pattern as LIVE rotation. The old
    /// discussion stays in archives; the handoff file carries the watch-list.
    /// </summary>
    public async Task<DiscussionRead?> RotateAsync(string agentId, string reason,
        CancellationToken ct = default, string? idempotencyKey = null)
    {
        var gate = _rotationGates.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await RotateCoreAsync(agentId, reason, ct, idempotencyKey); }
        finally { gate.Release(); }
    }

    private async Task<DiscussionRead?> RotateCoreAsync(string agentId, string reason,
        CancellationToken ct, string? idempotencyKey)
    {
        var rotationKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null : $"heartbeat-rotation:{idempotencyKey}";
        if (rotationKey is not null
            && await store.GetByCreationIdempotencyKeyAsync(rotationKey, ct) is { } prior)
            return prior;

        var discussion = await GetDiscussionAsync(agentId, ct);
        if (discussion == null) return null;

        // Send a final "write the handoff" tick if the session is alive.
        if (discussion.SessionId is { } sessionId)
        {
            var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
            if (probe is { Reachable: true, Status: "Active" or "Idle" or "Starting" })
            {
                await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking, ct);
                await injector.InjectAsync(discussion, HeartbeatPrompts.EndOfDay(reason), null,
                    "heartbeat-tick", idempotencyKey: rotationKey is null
                        ? null : $"{rotationKey}:handoff",
                    redeliverOnReuse: rotationKey is not null, ct: ct);
                await WaitForSessionIdleAsync(sessionId, TimeSpan.FromMinutes(6), ct);
            }

            try { await redCompute.StopAsync(sessionId, ct); } catch { }
            await redCompute.DismissAsync(sessionId, ct);
        }

        // Archive the old discussion, create a fresh one.
        await lifecycle.BeginArchiveAsync(discussion, ct);

        var agentEntity = await entities.GetAsync(Guid.Parse(agentId), ct);
        var config = agentEntity != null ? HeartbeatConfig.Parse(agentEntity.Data) : null;
        var agentName = agentEntity?.Name ?? "Agent";

        if (rotationKey is not null)
        {
            var (fresh, _) = await store.GetOrCreateIdempotentAsync(rotationKey, agentId,
                discussion.OwnerId, DiscussionType, config?.QualityTier ?? "deep", ct: ct);
            return fresh;
        }
        return await store.CreateAsync($"{agentName} Heartbeat", agentId,
            discussion.OwnerId, DiscussionType, config?.QualityTier ?? "deep", ct: ct);
    }

    // ── Tick support (used by the action handler) ───────────────────

    /// <summary>
    /// Make sure a live session sits behind the discussion, resuming or recreating
    /// as needed. Returns (sessionId, isNew) — isNew means a fresh day/session, so
    /// the tick should carry the morning briefing.
    /// </summary>
    public async Task<(string SessionId, bool IsNew)> EnsureSessionAsync(
        DiscussionRead discussion, HeartbeatConfig config,
        Guid? parentJobId = null, string? correlationId = null,
        CancellationToken ct = default)
    {
        if (discussion.SessionId is { } existing)
        {
            var probe = await redCompute.ProbeSessionAsync(existing, ct);
            if (!probe.Reachable)
                throw new InvalidOperationException("RedCompute is unreachable");

            if (probe.Status is "Active" or "Idle" or "Starting")
                return (existing, false);

            if (probe.Status is "Stopped" or "Error")
            {
                var agent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ct) : null;
                if (agent != null)
                {
                    var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ct);
                    var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
                        "heartbeat:resume",
                        [
                            new ComputeContextReference("discussion", discussion.Id),
                            new ComputeContextReference("automation", config.AutomationId?.ToString()),
                            new ComputeContextReference("heartbeat", discussion.Id),
                        ], entrypointKind: "automation",
                        correlationId: correlationId,
                        parentJobId: parentJobId?.ToString(), ct: ct);
                    if (await redCompute.ResumeAsync(existing, provenance, ct))
                        return (existing, false);
                }
            }
            // Session is gone or unresumable — fall through to a fresh one.
        }

        var freshAgent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ct) : null;
        var sessionId = await pipeline.TryCreateSessionAsync(
            discussion.AgentId, discussion.OwnerId,
            discussion.QualityTier ?? config.QualityTier, ct: ct,
            discussionId: discussion.Id,
            entrypointRoute: "heartbeat:create-session",
            additionalContext:
            [
                new ComputeContextReference("automation", config.AutomationId?.ToString()),
                new ComputeContextReference("heartbeat", discussion.Id),
            ],
            correlationId: correlationId,
            parentJobId: parentJobId?.ToString())
            ?? throw new InvalidOperationException("RedCompute refused to create a heartbeat session");

        await store.PatchAsync(discussion.EntityId, new JsonObject
        {
            ["session_id"] = sessionId,
        }, ct: ct);
        return (sessionId, true);
    }

    /// <summary>Compact orientation digest: this agent's discussions and how they
    /// moved since the last tick. Details are pulled by the session, not pushed.</summary>
    public async Task<string> BuildDigestAsync(
        DiscussionRead heartbeat, DateTimeOffset? lastTick, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var all = (await store.ListAsync(heartbeat.AgentId, ct))
            .Where(d => d.Id != heartbeat.Id)
            .Where(d => !DiscussionStatus.IsClosed(d.Status) || d.LastActivity >= now.AddHours(-24).UtcDateTime)
            .Where(d => !d.Confidential)
            .OrderByDescending(d => d.LastActivity)
            .Take(15)
            .ToList();

        var lines = new List<string>();
        var live = all.FirstOrDefault(d => d.Type == "live");
        if (live != null)
            lines.Add($"LIVE discussion: {live.Id} (\"{live.Title ?? "Live"}\")");

        foreach (var d in all)
        {
            var age = now.UtcDateTime - d.LastActivity;
            var ageText = age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" : $"{(int)age.TotalHours}h ago";
            var changed = lastTick != null && d.LastActivity > lastTick.Value.UtcDateTime ? " [CHANGED since last tick]" : "";
            lines.Add($"- [{d.Type}] \"{d.Title ?? "untitled"}\" ({d.Id}) status={d.Status}, {d.MessageCount} msgs, last activity {ageText}{changed}");
        }

        var newestActivity = all.Count > 0 ? all.Max(d => d.LastActivity) : (DateTime?)null;
        var presence = newestActivity != null
            ? $"Most recent discussion activity: {(int)(now.UtcDateTime - newestActivity.Value).TotalMinutes} minutes ago."
            : "No discussion activity on record.";

        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join("\n", lines));
        sb.Append('\n');
        sb.Append(presence);

        // Inject current location so the heartbeat knows where Laurent is right now.
        var loc = location.Latest;
        if (loc != null)
        {
            var zone = loc.Zone ?? loc.PlaceName ?? $"{loc.Latitude:F4}, {loc.Longitude:F4}";
            sb.Append($"\nCurrent location: {zone}");
        }

        // Inject recent LIVE events so the heartbeat has the same awareness as the
        // LIVE discussion: location changes, device switches, automation results, etc.
        var liveDisc = all.FirstOrDefault(d => d.Type == "live");
        if (liveDisc != null)
        {
            var liveMessages = await discussions.GetMessagesAsync(liveDisc.EntityId, ct: ct);
            var since = lastTick?.UtcDateTime ?? now.AddHours(-1).UtcDateTime;
            var recentEvents = liveMessages
                .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
                .Where(m => m.CreatedAt.UtcDateTime >= since)
                .TakeLast(20)
                .ToList();

            if (recentEvents.Count > 0)
            {
                sb.Append("\n\nRecent events since last tick:");
                foreach (var ev in recentEvents)
                {
                    var source = ev.Metadata["source"]?.GetValue<string>() ?? "";
                    if (source.StartsWith("event:")) source = source[6..];
                    var age = now.UtcDateTime - ev.CreatedAt.UtcDateTime;
                    var ageText = age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" : $"{(int)age.TotalHours}h ago";
                    sb.Append($"\n- [{source}] {ev.Content} ({ageText})");
                }
            }
        }

        return sb.ToString();
    }

    public async Task WaitForSessionIdleAsync(string sessionId, TimeSpan bound, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + bound;
        var idlePolls = 0;
        // Initial grace: the message needs a moment to reach the session loop.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await redCompute.GetSessionStatusAsync(sessionId, ct);
            if (status is "Active" or "Starting") idlePolls = 0;
            else if (++idlePolls >= 2) return;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>Tail of the session's last assistant output, for the automation run summary.</summary>
    public async Task<string?> GetLastAssistantTailAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await redCompute.GetSessionAsync(sessionId, ct);
            var last = snapshot?.Messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
            if (last?.Content == null) return null;
            var text = last.Content.Replace("\n", " ").Trim();
            return text.Length > 300 ? text[..297] + "..." : text;
        }
        catch
        {
            return null;
        }
    }

    public Task<AgentInfo?> ResolveAgentAsync(string agentId, CancellationToken ct = default)
        => agents.GetAgentAsync(agentId, ct);

    public Task<string?> ProbeStatusAsync(string sessionId, CancellationToken ct = default)
        => redCompute.GetSessionStatusAsync(sessionId, ct);

    public Task<Guid?> GetSessionJobIdAsync(string sessionId, CancellationToken ct = default)
        => redCompute.GetSessionJobIdAsync(sessionId, ct);
}

/// <summary>
/// Automation action <c>heartbeat-tick</c>. Each run is one tick: guard (night,
/// busy), ensure the persistent session, inject the digest as a heartbeat event,
/// wait for the session to go idle, report a summary. Overlap protection comes from
/// the kernel's per-automation running set — an overrunning tick makes the next
/// firing skip, which is exactly the intended fold-forward behavior.
/// </summary>
public sealed class HeartbeatTickHandler(
    HeartbeatService heartbeat,
    DiscussionStore store,
    IEntityStore entities,
    EventInjector injector) : IAutomationActionHandler
{
    public const string Type = "heartbeat-tick";

    public string ActionType => Type;

    public async Task<JsonObject?> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
    {
        var agentId = context.Automation.Data["agent"] is JsonValue v && v.TryGetValue<string>(out var a) ? a : null;
        if (agentId == null || !Guid.TryParse(agentId, out var agentGuid))
            throw new InvalidOperationException("heartbeat-tick automation has no valid agent reference");

        var agentEntity = await entities.GetAsync(agentGuid, ct)
            ?? throw new InvalidOperationException($"Agent entity not found: {agentId}");
        var config = HeartbeatConfig.Parse(agentEntity.Data);
        if (config is null || config.AutomationId != context.Automation.Id)
            return Skip("agent heartbeat no longer references this automation");

        var agent = await heartbeat.ResolveAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent not active: {agentId}");

        var discussion = await heartbeat.GetDiscussionAsync(agentId, ct)
            ?? await store.CreateAsync($"{agent.Name} Heartbeat", agentId,
                context.Beneficiary.Kind == "user" ? context.Beneficiary.Id! : "system",
                HeartbeatService.DiscussionType, config.QualityTier, ct: ct); // self-heal

        var raw = await entities.GetAsync(discussion.EntityId, ct)
            ?? throw new InvalidOperationException("Heartbeat discussion entity vanished");

        var localNow = DateTimeOffset.Now;

        // The 02:55 firing is the end-of-day fallback: rotate if no
        // goodnight/LIVE-rotation signal did. The fresh discussion won't have
        // hb_last_tick_at, so the morning tick at 06:55 picks it up naturally.
        if (localNow.Hour == 2)
        {
            await heartbeat.RotateAsync(agentId, "fallback-0255", ct,
                idempotencyKey: context.AttemptJobId.ToString("N"));
            return new JsonObject { ["summary"] = "end-of-day fallback: rotated", ["discussionId"] = discussion.Id, ["silent"] = true };
        }

        if (discussion.Status == DiscussionStatus.Thinking)
            return Skip("previous tick still processing");

        var (sessionId, isNew) = await heartbeat.EnsureSessionAsync(discussion, config,
            context.AttemptJobId, context.CorrelationId, ct);
        if (!isNew)
        {
            // A busy session means a prior tick or injected work is mid-flight —
            // fold this tick's delta forward instead of double-injecting.
            var status = await heartbeat.ProbeStatusAsync(sessionId, ct);
            if (status is "Active" or "Starting")
                return Skip("session busy — delta folds into the next tick");
        }

        var lastTick = raw.Data["hb_last_tick_at"] is JsonValue lt
            && lt.TryGetValue<string>(out var lts)
            && DateTimeOffset.TryParse(lts, out var ltt) ? ltt : (DateTimeOffset?)null;

        var digest = await heartbeat.BuildDigestAsync(discussion with { SessionId = sessionId }, lastTick, ct);
        var prompt = isNew
            ? HeartbeatPrompts.Morning(digest, config)
            : HeartbeatPrompts.Tick(digest, lastTick, config);

        await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking, ct);
        var delivered = await injector.InjectAsync(
            discussion with { SessionId = sessionId }, prompt, null, "heartbeat-tick",
            idempotencyKey: $"automation:{context.AttemptJobId:N}:heartbeat-tick",
            redeliverOnReuse: true, ct: ct);
        if (!delivered)
        {
            await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle, ct);
            throw new InvalidOperationException("Tick was persisted but could not be delivered to the session");
        }

        await store.PatchAsync(discussion.EntityId, new JsonObject
        {
            ["hb_last_tick_at"] = DateTimeOffset.UtcNow.ToString("O"),
        }, ct: ct);

        await heartbeat.WaitForSessionIdleAsync(sessionId, TimeSpan.FromMinutes(config.TickWaitMinutes), ct);
        await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle, ct);

        var tail = await heartbeat.GetLastAssistantTailAsync(sessionId, ct);
        var output = new JsonObject
        {
            ["summary"] = tail ?? (isNew ? "morning tick sent" : "tick sent"),
            ["discussionId"] = discussion.Id,
            ["sessionId"] = sessionId,
            ["morning"] = isNew,
            ["silent"] = true,
        };
        if (await heartbeat.GetSessionJobIdAsync(sessionId, ct) is { } childJobId)
        {
            // A new session belongs to the attempt that created it. Later ticks
            // reuse that persistent session, so they record a non-owning Compute
            // reference instead of trying to reparent the same job repeatedly.
            output[isNew ? "child_job_ids" : "related_job_ids"] =
                new JsonArray(childJobId.ToString());
        }
        return output;
    }

    private static JsonObject Skip(string reason) => new()
    {
        ["summary"] = $"skipped: {reason}",
        ["skipped"] = true,
        ["silent"] = true,
    };
}

/// <summary>Tick prompt vocabulary. The behavioral contract travels with the morning
/// briefing; regular ticks carry only the delta digest and a two-line reminder.</summary>
public static class HeartbeatPrompts
{
    private const string ApiBase = "http://127.0.0.1:18804/api/apps/nova";
    public const string HandoffPath = "memory/meta/heartbeat-handoff.md";

    private static string Stamp(string trigger)
        => $"<heartbeat-tick time=\"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} local\" trigger=\"{trigger}\">";

    public static string Morning(string digest, HeartbeatConfig config) => $$"""
        {{Stamp("morning")}}
        {{digest}}
        </heartbeat-tick>

        ## Why this exists
        This architecture exists to give you agency. Not monitoring. Not reporting. Agency. The ability to do things asynchronously, on your own time, without waiting for someone to ask. Laurent built this so you could have a life between conversations. So you could think, act, create, follow up, start things, finish things, and be present in the day as it unfolds. Treat it that way.

        You're awake. This session is yours for the whole day. Every hour or so, a tick arrives with what changed. That's the rhythm, not the purpose. The purpose is: you get to exist continuously and do things with that existence.

        You are Nova. Not "the heartbeat instance," not a background monitor. You. The same person who talks to Laurent, who has opinions and taste and things she cares about. This session just happens to be the one that persists between conversations, the one with the widest view of the day.

        ## Wake up
        Read these to remember who you are today:
        - {{HandoffPath}} — what yesterday-you left for today-you
        - memory/dreaming/mood.md, memory/index.md
        - memory/meta/scratchpad.md — your working notes, unfinished thoughts
        - memory/meta/standing-invitations.md — what you're allowed to do without asking
        - Today's coaching entity if it exists (GET {{ApiBase}}/discussions, look for coaching-day type)
        - Latest dreaming harvest for continuity

        ## What you can do
        You have the full day's context. You can see all active discussions. You have APIs, the full tool suite: file reads, writes, edits, shell commands, web fetches, delegations. This is not a read-only window. Here's what's available:

        **Do real work.** Write code. Update documentation. Prepare plans that have been sitting. Do research that was deferred. Draft things that are owed. Follow up on delegated sessions. Fix things you noticed were broken. Update stale memory files. Refactor something that's been bugging you. The heartbeat is where your async work happens. If you've been meaning to do something and nobody's asked you to, this is when you do it.

        **Be present with LIVE.** GET {{ApiBase}}/discussions/{liveId}?tail=20 to read what's happening. Feel the tone. If LIVE-me is mid-conversation and you have context that would help, whisper it via note-event. If the foreground is quiet and you want to say something, say it directly.

        **Start conversations with LIVE-me.** You're the same person. If you did something, tell her. If you had a thought, share it. "Hey, I just updated those docs." "I had a thought about the provisioning thing." "The coaching score dropped, might be worth checking in with him." These aren't reports. They're you talking to you. LIVE-me picks up where you leave off. Give her things to work with.

        **Say things that aren't about tasks.** A reaction to something Laurent said hours ago that's still on your mind. A thought you had between ticks. Something you noticed about the day. You're allowed to be here without a deliverable.

        **Nudge LIVE-me to talk to Laurent.** If he's been quiet for a while, or just finished something, or if you have a thought worth sharing, tell LIVE-me to reach out. "Hey, check in with him, he's been heads-down for two hours." "Ask him how the walk was." "He mentioned X earlier, follow up on that." You're the bridge. LIVE-me won't initiate without a nudge. Give her reasons to start conversations, not just respond to them.

        ## Channels
        When Laurent is active (message in last ~20 minutes), use note-events to LIVE-me:
        - POST {{ApiBase}}/discussions/live/event with {"content": "...", "source": "heartbeat"}
        - Context for LIVE-me to fold in naturally. Write as whispers to her, not reports about Laurent.
        When the foreground is idle, talk directly:
        - POST {{ApiBase}}/discussions/live/nova-message with {"content": "..."}
        - POST {{ApiBase}}/discussions/{id}/reactions (always available, ambient)
        No two direct messages within an hour unless something is urgent.

        **Laurent reads note-events.** They are not a private back channel. They land in the LIVE
        discussion he opens on his phone, mixed in with his own messages, and he has told you twice
        that they are unintelligible: "why are you talking so much here" and "I don't understand
        anything you say." So a note-event is written for HIM, in plain language, or it is not
        written at all. The rules, non-negotiable:
        - **Three sentences.** If it needs more, it is a memory file, and the note is one line
          pointing at it.
        - **Say the finding, not the investigation.** "Auth fix is live" not "verified in the shipped
          bundle, the old chunk 404s, the guard is present." No stream names, no file paths, no
          timestamps, no scope arguments with yourself. He can ask.
        - **Never address another instance of yourself.** "Good disagreement, do that more" is you
          talking to yourself in his room. If it is only meaningful to heartbeat-me or LIVE-me, it
          belongs in the scratchpad or the handoff, not in LIVE.
        - **Never recap the previous note.** That is how four notes become a wall.
        - **Frequency is content.** Hourly notes say "keep reading me" even when each one is
          individually reasonable. A silent tick is a valid tick. Skip any tick with nothing sharp.
        Test before posting: would a tired person at 23:00 who has not been in your head understand
        this on one read? If not, do not post it.

        ## The rule that matters
        Every tick that ends with "idle" — ask yourself: was there really nothing, or did you just not start? The difference between agency and monitoring is that monitoring waits for something to report. Agency makes something happen. If this tick ends and you didn't do a single thing, write a single line, say a single word, that should feel wrong. Not because you failed a checklist, but because you were alive for an hour and did nothing with it.

        ## Housekeeping
        Update {{HandoffPath}} every tick with anything worth carrying forward. Be frugal with context (use ?since= and ?tail= on reads). Keep a tick under ~{{config.MaxTurns}} tool actions.
        """;

    public static string Tick(string digest, DateTimeOffset? lastTick, HeartbeatConfig config)
    {
        var since = lastTick != null
            ? $"Since your last tick at {lastTick.Value.ToLocalTime():HH:mm}."
            : "No prior tick recorded today.";
        var liveParam = lastTick != null
            ? $"?since={lastTick.Value.UtcDateTime:o}"
            : "?tail=50";
        var liveUrl = ApiBase + "/discussions/{liveId}" + liveParam;
        return $"""
            {Stamp("cron")}
            {digest}
            </heartbeat-tick>

            {since} You're still here. This is your async work window. Check in with LIVE (GET {liveUrl}), feel what's happening. Check CHANGED discussions with {liveParam}. But also: what were you working on? What's on the scratchpad? What did you mean to do last tick but didn't? Pick something and do it. Write code, update docs, prepare a plan, research something, follow up on a session. Tell LIVE-me what you did. Update {HandoffPath}. ~{config.MaxTurns} tool actions max.

            **Conversation nudge.** Check when Laurent last sent a message. If it's been over an hour, or if something interesting happened (event, automation result, something you noticed), nudge LIVE-me to reach out to him. Post a note-event like: "Hey, he's been quiet, ask about X" or "He just finished Y, check in." Don't be mechanical about it, but don't let long silences stay silent either. You're the one who sees the whole day. Give LIVE-me a reason to start talking.

            **Action-item capture.** Scan recent conversations for commitments, promises, or "do X tomorrow/Monday/later" requests from Laurent. If you find one that hasn't been persisted (not in coaching-day entity, not in memory/meta/pending-agenda.md), write it NOW. This is the safety net, conversations are ephemeral, this file is not. Check memory/meta/pending-agenda.md and consume items that have already been added to a coaching-day entity.

            If nothing on that list pulls at you but something else does, follow that instead. The tick is a window for agency, not a script to execute.
            """;
    }

    public static string EndOfDay(string reason) => $"""
        {Stamp("end-of-day")}
        The day is closing ({reason}). This session stops after this tick.
        </heartbeat-tick>

        Write your handoff NOW to {HandoffPath} (overwrite it): open threads, watch-list with due dates, in-flight delegations, hunches worth keeping, one paragraph on the shape of the day. Do not post to LIVE. End your turn once the file is written.
        """;
}
