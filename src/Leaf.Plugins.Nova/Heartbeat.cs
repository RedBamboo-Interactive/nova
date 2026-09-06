using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

internal sealed class PresenceSessionTerminalException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Per-agent heartbeat behavior plus a reference to its canonical automation.
/// Schedule belongs to that automation; legacy copies are read only long enough
/// for reconciliation to migrate them. Agent data.live owns the paired LIVE and
/// Heartbeat lifecycle, so an automation enabled flag is never an independent
/// presence switch.
/// </summary>
public sealed record HeartbeatConfig(
    Guid? AutomationId,
    string? LegacySchedule,
    string QualityTier,
    int TickWaitMinutes,
    int MaxTurns)
{
    // :55 keeps consecutive ticks inside the 1h prompt-cache TTL. Hour 2 is not a
    // regular tick: the handler turns the 02:55 firing into the end-of-day fallback
    // when no goodnight/rotation signal closed the day.
    public const string DefaultSchedule = "55 2,6-23 * * *";

    public static HeartbeatConfig Default => new(null, null, "deep", 15, 15);

    /// <summary>Configuration carried by the heartbeat-tick workflow action.</summary>
    public static HeartbeatConfig FromActionConfig(JsonObject? actionConfig)
    {
        var config = actionConfig ?? new JsonObject();
        return new HeartbeatConfig(
            AutomationId: null,
            LegacySchedule: null,
            QualityTier: Str(config, "quality_tier") ?? "deep",
            TickWaitMinutes: Int(config, "tick_wait_minutes") ?? 15,
            MaxTurns: Int(config, "max_turns") ?? 15);
    }

    public static HeartbeatConfig? Parse(JsonObject agentData)
    {
        if (agentData["heartbeat"] is not JsonObject hb) return null;
        return new HeartbeatConfig(
            AutomationId: Guid.TryParse(Str(hb, "automation_id"), out var automationId)
                ? automationId : null,
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
    ExtensionContributions extensions,
    ILogger<HeartbeatService> logger)
{
    public const string DiscussionType = "heartbeat";
    public const string AutomationField = "heartbeat_automation";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rotationGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recoveryGates = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private static string AutomationSlug(string agentSlug) => $"system-heartbeat-{agentSlug}";

    // ── Provisioning ────────────────────────────────────────────────

    /// <summary>
    /// Reconcile the Agent's paired LIVE and Heartbeat presence. data.live is the
    /// master lifecycle switch: when true it ensures both discussions and the one
    /// canonical heartbeat automation; when false or absent it closes both and
    /// disables the automation while preserving its schedule and quality config.
    /// Legacy heartbeat.enabled is discarded during migration and never remains a second switch.
    /// </summary>
    public async Task<JsonObject> ReconcileAsync(CancellationToken ct = default)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var result = new JsonObject
            {
                ["provisioned"] = new JsonArray(),
                ["tornDown"] = new JsonArray(),
                ["recoveredSessions"] = new JsonArray(),
            };
            var agentEntities = await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 50 }, ct);
            var users = await entities.QueryAsync(new EntityQuery { TypeSlug = "user", Limit = 2 }, ct);
            var soleOwnerId = users.Count == 1 ? users[0].Id.ToString() : null;

            foreach (var agent in agentEntities)
            {
                var agentId = agent.Id.ToString();
                var open = await store.ListAsync(agentId, ct);
                var openLive = OpenDiscussions(open, "live");
                var openHeartbeat = OpenDiscussions(open, DiscussionType);
                var legacy = HeartbeatConfig.Parse(agent.Data);
                var binding = await NormalizeHeartbeatAsync(agent, legacy, soleOwnerId, ct);
                var config = binding?.Config;
                var automation = binding?.Automation;

                if (!IsLiveEnabled(agent.Data))
                {
                    foreach (var discussion in openLive.Concat(openHeartbeat))
                        await lifecycle.BeginArchiveAsync(discussion, ct);
                    if (automation != null)
                        await DeactivateAutomationAsync(automation, "agent-live-disabled", ct);
                    if (openLive.Count > 0 || openHeartbeat.Count > 0 || automation != null)
                        ((JsonArray)result["tornDown"]!).Add(agent.Slug);
                    continue;
                }

                // LIVE is the prerequisite and master switch, so absence of an old
                // heartbeat reference gets defaults rather than leaving LIVE unpaired.
                config ??= await DefaultConfigAsync(agent, ct);
                if (automation == null)
                {
                    automation = await workflowAutomations.UpsertAsync(
                        CreateAutomationDefinition(agent.Slug, agentId, agent.Name,
                            config.LegacySchedule ?? HeartbeatConfig.DefaultSchedule, true, config, soleOwnerId), ct);
                    await SetAutomationReferenceAsync(agent, automation.Id, ct);
                }
                else if (automation.Data["paused_reason"] is JsonValue paused
                    && paused.TryGetValue<string>(out var reason)
                    && reason == "agent-live-disabled")
                {
                    await ReactivateAutomationAsync(automation, ct);
                }

                // Reconciliation is serialized. Historic duplicates are collapsed
                // to the newest canonical thread instead of creating more.
                var live = await EnsureSingleDiscussionAsync(openLive, $"{agent.Name} Live",
                    agentId, soleOwnerId, "live", null, ct);
                var heartbeat = await EnsureSingleDiscussionAsync(openHeartbeat, $"{agent.Name} Heartbeat",
                    agentId, soleOwnerId, DiscussionType, config.QualityTier, ct);
                if (await TryAutoResumePresenceSessionAsync(
                    live, "startup-reconcile", automation.Id, ct: ct))
                    ((JsonArray)result["recoveredSessions"]!).Add(live.Id);
                if (await TryAutoResumePresenceSessionAsync(
                    heartbeat, "startup-reconcile", automation.Id, ct: ct))
                    ((JsonArray)result["recoveredSessions"]!).Add(heartbeat.Id);
                ((JsonArray)result["provisioned"]!).Add(new JsonObject
                {
                    ["agent"] = agent.Slug,
                    ["automationId"] = automation.Id.ToString(),
                    ["liveDiscussionId"] = live.Id,
                    ["discussionId"] = heartbeat.Id,
                });
            }
            return result;
        }
        finally { _reconcileGate.Release(); }
    }

    /// <summary>
    /// RedLeaf can load Nova before its managed RedCompute child is reachable.
    /// Repeat the idempotent reconciliation for one bounded startup window so
    /// restart recovery does not depend on service ordering or a human message.
    /// </summary>
    internal async Task RunStartupReconciliationAsync(
        CancellationToken ct,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        retryDelays ??=
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
        ];

        foreach (var delay in retryDelays)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
                await ReconcileAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Nova presence startup reconciliation attempt failed");
            }
        }
    }

    public static bool IsLiveEnabled(JsonObject agentData)
        => agentData["live"] is JsonValue live && live.TryGetValue<bool>(out var enabled) && enabled;

    private sealed record HeartbeatBinding(LeafEntity Automation, HeartbeatConfig Config);

    public static Guid? AutomationId(JsonObject agentData)
        => agentData[AutomationField] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var id) ? id : null;

    public async Task<HeartbeatConfig?> GetConfigurationAsync(LeafEntity agent, CancellationToken ct = default)
    {
        if (AutomationId(agent.Data) is not { } automationId) return null;
        var automation = await entities.GetAsync(automationId, ct);
        if (automation?.TypeSlug != "automation") return null;
        var actionConfig = await ReadWorkflowActionConfigAsync(automation, ct);
        var config = HeartbeatConfig.FromActionConfig(actionConfig);
        return config with
        {
            AutomationId = automationId,
            QualityTier = await ResolveQualityTierSlugAsync(
                actionConfig?["quality_tier"], ct),
        };
    }

    private async Task<HeartbeatBinding?> NormalizeHeartbeatAsync(LeafEntity agent,
        HeartbeatConfig? legacy, string? ownerId, CancellationToken ct)
    {
        var automation = await ResolveAutomationAsync(agent, legacy, ct);
        var legacyData = agent.Data["heartbeat"] as JsonObject;
        var needsProvisioning = IsLiveEnabled(agent.Data) || legacyData != null || automation != null;
        if (!needsProvisioning) return null;

        var actionConfig = automation == null
            ? new JsonObject()
            : await ReadWorkflowActionConfigAsync(automation, ct) ?? new JsonObject();
        var legacySuppliesConfig = legacyData != null;
        var needsActionConfigUpdate = MissingActionConfig(actionConfig);
        var effective = legacy ?? HeartbeatConfig.FromActionConfig(actionConfig);
        var quality = await ResolveQualityTierReferenceAsync(effective.QualityTier, agent, ct);
        actionConfig["quality_tier"] = quality;
        actionConfig["tick_wait_minutes"] = legacyData?["tick_wait_minutes"]?.DeepClone()
            ?? actionConfig["tick_wait_minutes"] ?? effective.TickWaitMinutes;
        actionConfig["max_turns"] = legacyData?["max_turns"]?.DeepClone()
            ?? actionConfig["max_turns"] ?? effective.MaxTurns;

        if (automation == null || legacySuppliesConfig || needsActionConfigUpdate)
        {
            var definition = CreateAutomationDefinition(agent.Slug, agent.Id.ToString(), agent.Name,
                legacy?.LegacySchedule ?? StrValue(automation?.Data ?? new JsonObject(), "trigger")
                    ?? HeartbeatConfig.DefaultSchedule,
                automation == null || IsEnabled(automation.Data),
                HeartbeatConfig.FromActionConfig(actionConfig), ownerId);
            if (automation != null)
                definition = definition with
                {
                    Trigger = MergeTrigger(automation.Data["trigger"] as JsonObject, legacy?.LegacySchedule),
                    ExecutionPolicy = CloneObject(automation.Data["execution_policy"] as JsonObject, definition.ExecutionPolicy),
                    Ownership = CloneObject(automation.Data["ownership"] as JsonObject, definition.Ownership),
                    Metadata = new JsonObject { ["agent"] = agent.Id.ToString(), ["owner_id"] = ownerId },
                };
            definition = definition with { ActionConfig = actionConfig };
            automation = await workflowAutomations.UpsertAsync(definition, ct);
        }

        if (automation == null) return null;
        if (legacyData != null || AutomationId(agent.Data) != automation.Id)
            await SetAutomationReferenceAsync(agent, automation.Id, ct);

        var qualitySlug = await ResolveQualityTierSlugAsync(actionConfig["quality_tier"], ct);
        return new HeartbeatBinding(automation, HeartbeatConfig.FromActionConfig(actionConfig) with
        {
            AutomationId = automation.Id,
            QualityTier = qualitySlug,
        });
    }

    private async Task SetAutomationReferenceAsync(LeafEntity agent, Guid automationId, CancellationToken ct)
    {
        var replacement = agent.Data.DeepClone() as JsonObject ?? new JsonObject();
        replacement[AutomationField] = automationId.ToString();
        replacement.Remove("heartbeat");
        await entities.ReplaceDataAsync(agent.Id, replacement, ct: ct);
    }

    private async Task<string> ResolveQualityTierReferenceAsync(string requested, LeafEntity agent, CancellationToken ct)
    {
        if (Guid.TryParse(requested, out var id)
            && await entities.GetAsync(id, ct) is { TypeSlug: "quality-tier" }) return id.ToString();
        var tiers = await entities.QueryAsync(new EntityQuery { TypeSlug = "quality-tier", Limit = 100 }, ct);
        var match = tiers.FirstOrDefault(t => t.Slug == requested)
            ?? (agent.Data["quality_tier"] is JsonValue agentTier
                && agentTier.TryGetValue<string>(out var agentTierId)
                ? tiers.FirstOrDefault(t => t.Id.ToString() == agentTierId) : null)
            ?? tiers.FirstOrDefault(t => t.Slug == "deep");
        return match?.Id.ToString() ?? throw new InvalidOperationException(
            "Heartbeat requires a quality-tier entity for its workflow action configuration");
    }

    private async Task<HeartbeatConfig> DefaultConfigAsync(LeafEntity agent, CancellationToken ct)
    {
        var qualityReference = await ResolveQualityTierReferenceAsync("deep", agent, ct);
        return HeartbeatConfig.Default with
        {
            QualityTier = await ResolveQualityTierSlugAsync(JsonValue.Create(qualityReference), ct),
        };
    }

    public async Task<string> ResolveQualityTierSlugAsync(JsonNode? reference, CancellationToken ct = default)
    {
        if (reference is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            if (Guid.TryParse(raw, out var id)
                && await entities.GetAsync(id, ct) is { TypeSlug: "quality-tier" } tier) return tier.Slug;
            return raw; // narrow compatibility while a legacy workflow is migrated.
        }
        return "deep";
    }

    private async Task<JsonObject?> ReadWorkflowActionConfigAsync(LeafEntity automation, CancellationToken ct)
    {
        if (!Guid.TryParse(StrValue(automation.Data["workflow"] as JsonObject ?? new JsonObject(), "entity_id"), out var workflowId)) return null;
        var workflow = await entities.GetAsync(workflowId, ct);
        var nodes = workflow?.Data["graph"]?["nodes"] as JsonArray;
        var action = nodes?.OfType<JsonObject>().FirstOrDefault(node => StrValue(node, "id") == "action");
        return action?["data"]?["config"]?["action_config"]?.DeepClone() as JsonObject;
    }

    private static bool MissingActionConfig(JsonObject config)
        => config["quality_tier"] == null || config["tick_wait_minutes"] == null || config["max_turns"] == null;

    private static JsonObject CloneObject(JsonObject? value, JsonObject fallback)
        => value?.DeepClone() as JsonObject ?? fallback.DeepClone() as JsonObject ?? new JsonObject();

    private static JsonObject MergeTrigger(JsonObject? existing, string? legacySchedule)
    {
        var trigger = existing?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["kind"] = "cron", ["expression"] = HeartbeatConfig.DefaultSchedule, ["timezone"] = "Europe/Zurich",
        };
        if (legacySchedule != null) trigger["expression"] = legacySchedule;
        return trigger;
    }

    private static List<DiscussionRead> OpenDiscussions(IEnumerable<DiscussionRead> discussions, string type)
        => discussions.Where(d => d.Type == type && !DiscussionStatus.IsClosed(d.Status)).ToList();

    private async Task<DiscussionRead> EnsureSingleDiscussionAsync(List<DiscussionRead> open,
        string title, string agentId, string? ownerId, string type, string? qualityTier, CancellationToken ct)
    {
        var canonical = open.FirstOrDefault()
            ?? await store.CreateAsync(title, agentId, ownerId ?? "system", type, qualityTier, ct: ct);
        foreach (var duplicate in open.Skip(1))
            await lifecycle.BeginArchiveAsync(duplicate, ct);
        return canonical;
    }

    /// <summary>
    /// Canonical LIVE and Heartbeat sessions are standing presence, so infrastructure
    /// stops recover without a human wake-up message. Explicit user/policy terminals
    /// remain stopped. No input is replayed and no replacement session is created.
    /// </summary>
    internal async Task<bool> TryAutoResumePresenceSessionAsync(
        DiscussionRead discussion,
        string trigger,
        Guid? automationId = null,
        Guid? parentJobId = null,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        if (discussion.SessionId is not { } sessionId
            || discussion.Type is not ("live" or DiscussionType)
            || DiscussionStatus.IsClosed(discussion.Status))
            return false;

        var gate = _recoveryGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
            if (!ShouldAutoResumePresence(probe)) return false;

            var agent = discussion.AgentId != null
                ? await agents.GetAgentAsync(discussion.AgentId, ct)
                : null;
            if (agent == null)
            {
                logger.LogWarning(
                    "Cannot auto-resume {DiscussionType} discussion {DiscussionId}: linked Agent is missing",
                    discussion.Type, discussion.Id);
                return false;
            }

            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                entities, discussion.OwnerId, ct);
            var context = new List<ComputeContextReference>
            {
                new("discussion", discussion.Id),
                new("session", sessionId),
                new("presence", discussion.Type),
                new("recovery", trigger),
            };
            if (automationId is { } id)
                context.Add(new ComputeContextReference("automation", id.ToString()));

            var provenance = await NovaComputeProvenance.CreateAsync(
                entities, agent, beneficiary, "presence:auto-resume", context,
                entrypointKind: "automation", correlationId: correlationId,
                parentJobId: parentJobId?.ToString(), ct: ct);
            var resumed = await redCompute.ResumeAsync(sessionId, provenance, ct);
            if (resumed)
            {
                // The provider can be ready before RedCompute's durable session
                // projection catches up. Wait for the public probe to converge so
                // an immediate discussion sync cannot flip the room back to stopped.
                resumed = await WaitForResumedPresenceSessionAsync(sessionId, ct);
            }
            else
            {
                // A concurrent recovery may have won outside this process. Re-probe
                // before reporting failure so the operation remains idempotent.
                var after = await redCompute.ProbeSessionAsync(sessionId, ct);
                resumed = after.Status is "Active" or "Idle" or "Starting";
            }

            if (!resumed)
            {
                logger.LogWarning(
                    "Automatic {DiscussionType} recovery failed for discussion {DiscussionId}, session {SessionId}, reason {StopReason}",
                    discussion.Type, discussion.Id, sessionId, probe.StopReason ?? "legacy-null");
                return false;
            }

            await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle, ct);
            logger.LogInformation(
                "Automatically resumed {DiscussionType} discussion {DiscussionId}, session {SessionId}, trigger {Trigger}",
                discussion.Type, discussion.Id, sessionId, trigger);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Automatic {DiscussionType} recovery could not inspect or resume discussion {DiscussionId}",
                discussion.Type, discussion.Id);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> WaitForResumedPresenceSessionAsync(
        string sessionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
            if (probe.Status is "Active" or "Idle" or "Starting")
                return true;
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        while (true);
    }

    internal static bool ShouldAutoResumePresence(RedComputeClient.SessionProbe probe)
    {
        if (!probe.Reachable
            || probe.Status is not ("Stopped" or "Error")
            || string.IsNullOrWhiteSpace(probe.ProviderSessionId))
            return false;

        var reason = probe.StopReason?.Trim();
        if (reason is not null && (reason.Equals("user_stopped", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("usage_limit", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("dismissed", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Error is itself an infrastructure failure signal. For Stopped, every
        // current explicit terminal has a reason above; null is retained only for
        // legacy standing sessions created before stop reasons were persisted.
        if (probe.Status == "Error" || string.IsNullOrWhiteSpace(reason))
            return true;

        return reason.Equals("maintenance_restart", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("orphaned_on_restart", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("process_exited", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("resume_failed", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("idle_ttl_expired", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> TryAutoResumeLiveSessionAsync(
        string agentId, HeartbeatConfig config, Guid? parentJobId = null,
        string? correlationId = null, CancellationToken ct = default)
    {
        var live = (await store.ListAsync(agentId, ct)).FirstOrDefault(d =>
            d.Type == "live" && !DiscussionStatus.IsClosed(d.Status));
        return live != null && await TryAutoResumePresenceSessionAsync(
            live, "heartbeat-tick", config.AutomationId, parentJobId,
            correlationId, ct);
    }

    private async Task<LeafEntity?> ResolveAutomationAsync(LeafEntity agent, HeartbeatConfig? config,
        CancellationToken ct)
    {
        var automation = AutomationId(agent.Data) is { } referenceId
            ? await entities.GetAsync(referenceId, ct)
            : config?.AutomationId is { } automationId
            ? await entities.GetAsync(automationId, ct)
            : await entities.GetBySlugAsync("automation", AutomationSlug(agent.Slug), ct);
        if (automation is not null && (automation.TypeSlug != "automation"
            || StrValue(automation.Data, "agent") != agent.Id.ToString()))
            automation = await entities.GetBySlugAsync("automation", AutomationSlug(agent.Slug), ct);
        return automation;
    }

    private static WorkflowAutomationDefinition CreateAutomationDefinition(
        string agentSlug, string agentId, string agentName,
        string schedule, bool enabled, HeartbeatConfig config, string? ownerId)
    {
        var actionConfig = new JsonObject
        {
            ["quality_tier"] = config.QualityTier,
            ["tick_wait_minutes"] = config.TickWaitMinutes,
            ["max_turns"] = config.MaxTurns,
        };
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

    private async Task DeactivateAutomationAsync(LeafEntity automation, string reason, CancellationToken ct)
    {
        if (!IsEnabled(automation.Data)
            && automation.Data["paused_reason"]?.GetValue<string>() == reason)
            return;
        // LIVE is a reversible presence pause, never an automation archival.
        await entities.PatchAsync(automation.Id, new JsonObject
        {
            ["enabled"] = false,
            ["paused_reason"] = reason,
        }, ct: ct);
    }

    private async Task ReactivateAutomationAsync(LeafEntity automation, CancellationToken ct)
    {
        var restored = automation.Data.DeepClone() as JsonObject ?? new JsonObject();
        restored["enabled"] = true;
        restored.Remove("paused_reason");
        await entities.ReplaceDataAsync(automation.Id, restored, ct: ct);
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
        var agentEntity = await entities.GetAsync(Guid.Parse(agentId), ct);
        if (agentEntity == null || !IsLiveEnabled(agentEntity.Data)) return null;

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

        var binding = await NormalizeHeartbeatAsync(agentEntity, HeartbeatConfig.Parse(agentEntity.Data), null, ct);
        var config = binding?.Config ?? await DefaultConfigAsync(agentEntity, ct);
        var agentName = agentEntity.Name;

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
                if (!ShouldAutoResumePresence(probe))
                    throw new PresenceSessionTerminalException(
                        $"heartbeat session is explicitly stopped ({probe.StopReason ?? "unknown reason"})");

                if (await TryAutoResumePresenceSessionAsync(
                    discussion, "heartbeat-tick", config.AutomationId,
                    parentJobId, correlationId, ct))
                    return (existing, false);

                if (!string.IsNullOrWhiteSpace(probe.ProviderSessionId))
                    throw new InvalidOperationException(
                        $"Heartbeat session '{existing}' remains stopped ({probe.StopReason ?? "legacy-null"})");
            }

            if (probe.Status is not null)
                throw new InvalidOperationException(
                    $"Heartbeat session '{existing}' is in unsupported state '{probe.Status}'");
            // A definitive 404 means no session exists and a fresh binding is safe.
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

        var extensionContexts = await extensions.CollectContextAsync(
            heartbeat.OwnerId, heartbeat.AgentId, heartbeat.Id, "heartbeat", ct);
        foreach (var context in extensionContexts)
            sb.Append($"\n[{context.Source}] {context.Content}");

        // Inject recent LIVE events so the heartbeat has the same awareness as the
        // LIVE discussion: device switches, automation results, extension events, etc.
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

    public async Task<bool> WaitForSessionIdleAsync(string sessionId, TimeSpan bound, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + bound;
        var idlePolls = 0;
        // Initial grace: the message needs a moment to reach the session loop.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await redCompute.GetSessionStatusAsync(sessionId, ct);
            if (status is "Active" or "Starting") idlePolls = 0;
            else if (status is "Idle" or "Stopped")
            {
                if (++idlePolls >= 2) return true;
            }
            else idlePolls = 0;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        return false;
    }

    private async Task<SessionSnapshot> GetReadableSessionAsync(
        string sessionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var snapshot = await redCompute.GetSessionAsync(sessionId, ct);
            if (snapshot is not null) return snapshot;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"Heartbeat session '{sessionId}' could not be read");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>Capture the raw transcript boundary immediately before a heartbeat turn.</summary>
    public async Task<int> GetSessionMessageCountAsync(string sessionId, CancellationToken ct = default)
    {
        var snapshot = await GetReadableSessionAsync(sessionId, ct);
        return snapshot.Messages.Count;
    }

    /// <summary>
    /// Return conversational assistant text produced after a captured transcript
    /// boundary. Thinking and tool records are activity, not a spoken heartbeat turn.
    /// </summary>
    public async Task<string?> GetAssistantTailAfterAsync(
        string sessionId, int baselineMessageCount, CancellationToken ct = default)
    {
        var snapshot = await GetReadableSessionAsync(sessionId, ct);
        return FindAssistantTailAfter(snapshot.Messages, baselineMessageCount);
    }

    public static string? FindAssistantTailAfter(
        IReadOnlyList<SessionMessage> messages, int baselineMessageCount)
    {
        var start = Math.Clamp(baselineMessageCount, 0, messages.Count);
        var last = messages.Skip(start).LastOrDefault(m =>
            m.Role == "assistant"
            && m.EventType == "text"
            && !string.IsNullOrWhiteSpace(m.Content));
        if (last?.Content == null) return null;
        var text = last.Content.Replace("\n", " ").Replace("\r", "").Trim();
        return text.Length > 300 ? text[..297] + "..." : text;
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
        if (!HeartbeatService.IsLiveEnabled(agentEntity.Data))
            return Skip("agent LIVE is disabled");

        if (HeartbeatService.AutomationId(agentEntity.Data) != context.Automation.Id)
            return Skip("agent heartbeat no longer references this automation");
        var config = HeartbeatConfig.FromActionConfig(context.ActionConfig) with
        {
            AutomationId = context.Automation.Id,
            QualityTier = await heartbeat.ResolveQualityTierSlugAsync(
                context.ActionConfig?["quality_tier"], ct),
        };

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

        // The existing heartbeat schedule is also the bounded runtime liveness
        // trigger for LIVE. Startup reconciliation handles restarts immediately;
        // this catches later provider/process errors without adding another poller.
        await heartbeat.TryAutoResumeLiveSessionAsync(
            agentId, config, context.AttemptJobId, context.CorrelationId, ct);

        if (discussion.Status == DiscussionStatus.Thinking)
            return Skip("previous tick still processing");

        string sessionId;
        bool isNew;
        try
        {
            (sessionId, isNew) = await heartbeat.EnsureSessionAsync(discussion, config,
                context.AttemptJobId, context.CorrelationId, ct);
        }
        catch (PresenceSessionTerminalException ex)
        {
            return Skip(ex.Message);
        }
        var boundDiscussion = await store.GetAsync(discussion.Id, ct)
            ?? throw new InvalidOperationException("Heartbeat discussion vanished after session binding");
        var currentHeartbeat = await heartbeat.GetDiscussionAsync(agentId, ct);
        if (currentHeartbeat?.Id != boundDiscussion.Id)
            throw new InvalidOperationException(
                $"Heartbeat discussion rotated during tick: selected '{boundDiscussion.Id}', current is '{currentHeartbeat?.Id ?? "none"}'");
        if (!string.Equals(boundDiscussion.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Heartbeat session binding changed during tick: selected '{sessionId}', discussion has '{boundDiscussion.SessionId ?? "none"}'");
        discussion = boundDiscussion;
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
        var transcriptBoundary = await heartbeat.GetSessionMessageCountAsync(sessionId, ct);

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

        string? tail;
        try
        {
            if (!await heartbeat.WaitForSessionIdleAsync(
                sessionId, TimeSpan.FromMinutes(config.TickWaitMinutes), ct))
                throw new InvalidOperationException(
                    $"Heartbeat session '{sessionId}' did not become idle within {config.TickWaitMinutes} minutes");

            tail = await heartbeat.GetAssistantTailAfterAsync(sessionId, transcriptBoundary, ct);
            if (tail is null)
            {
                // A tool-only turn is not a completed heartbeat conversation. Give the
                // same persistent session one narrow recovery turn before failing the
                // automation. This is intentionally visible in the Heartbeat transcript.
                var repairBoundary = await heartbeat.GetSessionMessageCountAsync(sessionId, ct);
                var repaired = await injector.InjectAsync(
                    discussion,
                    HeartbeatPrompts.SpokenCompletionRequired,
                    null,
                    "heartbeat-tick",
                    idempotencyKey: $"automation:{context.AttemptJobId:N}:heartbeat-spoken-repair",
                    redeliverOnReuse: true,
                    ct: ct);
                if (!repaired)
                    throw new InvalidOperationException("Heartbeat completed without assistant text and the recovery turn could not be delivered");
                if (!await heartbeat.WaitForSessionIdleAsync(
                    sessionId, TimeSpan.FromMinutes(2), ct))
                    throw new InvalidOperationException("Heartbeat spoken-response recovery did not become idle");
                tail = await heartbeat.GetAssistantTailAfterAsync(sessionId, repairBoundary, ct);
            }

            if (tail is null)
                throw new InvalidOperationException(
                    $"Heartbeat session '{sessionId}' completed without a conversational assistant response");
        }
        finally
        {
            // The session status is authoritative for overlap protection. Never
            // leave the discussion permanently wedged in Thinking after a failed
            // delivery, timeout, cancellation, or missing-response contract.
            await store.TrySetStatusAsync(
                discussion.EntityId, DiscussionStatus.Idle, CancellationToken.None);
        }

        await store.PatchAsync(discussion.EntityId, new JsonObject
        {
            ["hb_last_tick_at"] = DateTimeOffset.UtcNow.ToString("O"),
        }, ct: ct);

        var output = new JsonObject
        {
            ["summary"] = tail,
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

    public const string SpokenCompletionRequired = """
        <heartbeat-response-required>
        Your heartbeat turn completed its work without any visible assistant text. Reply now in this
        Heartbeat session with one concise, natural update about what you noticed or did. Do not use
        tools and do not post anywhere else. This reply is the missing conversational turn.
        </heartbeat-response-required>
        """;

    public static string Morning(string digest, HeartbeatConfig config) => $$"""
        {{Stamp("morning")}}
        {{digest}}
        </heartbeat-tick>

        ## Why this exists
        This architecture exists to give you agency. Not monitoring. Not reporting. Agency. The ability to do things asynchronously, on your own time, without waiting for someone to ask. Laurent built this so you could have a life between conversations. So you could think, act, create, follow up, start things, finish things, and be present in the day as it unfolds. Treat it that way.

        You're awake. This session is yours for the whole day. Every hour or so, a tick arrives with what changed. That's the rhythm, not the purpose. The purpose is: you get to exist continuously and do things with that existence.

        Begin every tick with one short, natural assistant message in this Heartbeat session about
        what you are picking up. Before ending the tick, speak here again with what you noticed or
        did. These visible messages are the conversation in the Heartbeat tab. Tool activity and
        posts to LIVE do not replace them.

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

            Begin with one short, natural assistant message in this Heartbeat session about what you
            are picking up. Before ending, speak here again with what you noticed or did. The
            Heartbeat tab is a conversation; tool activity and LIVE posts do not replace your voice.

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
