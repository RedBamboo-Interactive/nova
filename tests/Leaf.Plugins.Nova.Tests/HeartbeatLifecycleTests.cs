using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class HeartbeatLifecycleTests
{
    [Fact]
    public void Nova_seed_defines_the_agent_live_field()
    {
        var seedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "seeds", "agent-fields.json"));
        var fields = JsonNode.Parse(File.ReadAllText(seedPath))!.AsArray().OfType<JsonObject>().ToList();
        var field = Assert.Single(fields, field => field["slug"]?.GetValue<string>() == "live");

        Assert.Equal("agent", field["parentType"]!.GetValue<string>());
        Assert.Equal("live", field["slug"]!.GetValue<string>());
        Assert.Equal("Live", field["name"]!.GetValue<string>());
        Assert.Equal("boolean", field["fieldType"]!.GetValue<string>());
        Assert.Equal(9, field["sortOrder"]!.GetValue<int>());
        Assert.Equal("ph-bold ph-broadcast", field["icon"]!.GetValue<string>());
        Assert.Contains("paired LIVE and Heartbeat", field["description"]!.GetValue<string>());
        Assert.DoesNotContain("schedule", field["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quality", field["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var automation = Assert.Single(fields, field => field["slug"]?.GetValue<string>() == "heartbeat_automation");
        Assert.Equal("entity_ref", automation["fieldType"]!.GetValue<string>());
        Assert.Equal(10, automation["sortOrder"]!.GetValue<int>());
        Assert.Equal("{\"target_type\": \"automation\"}", automation["constraints"]!.GetValue<string>());
    }

    [Fact]
    public void Heartbeat_tick_seed_exposes_typed_action_configuration_without_changing_nova_session()
    {
        var seedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "seeds", "node-types.json"));
        var nodes = JsonNode.Parse(File.ReadAllText(seedPath))!.AsArray().OfType<JsonObject>().ToList();
        var heartbeat = Assert.Single(nodes, node => node["slug"]?.GetValue<string>() == HeartbeatTickHandler.Type);
        var inputs = heartbeat["data"]!["input_ports"]!.AsArray().OfType<JsonObject>().ToList();
        var quality = Assert.Single(inputs, port => port["id"]?.GetValue<string>() == "quality_tier");
        Assert.Equal("entity_ref", quality["field_type"]!.GetValue<string>());
        Assert.Equal("quality-tier", quality["constraints"]!["target_type"]!.GetValue<string>());
        Assert.Equal("action_config", quality["config_path"]!.GetValue<string>());
        Assert.Equal("integer", Assert.Single(inputs, port => port["id"]?.GetValue<string>() == "tick_wait_minutes")["field_type"]!.GetValue<string>());
        Assert.Equal("integer", Assert.Single(inputs, port => port["id"]?.GetValue<string>() == "max_turns")["field_type"]!.GetValue<string>());

        var novaSession = Assert.Single(nodes, node => node["slug"]?.GetValue<string>() == "nova-session");
        var actionConfig = novaSession["data"]!["config_schema"]!["properties"]!["action_config"]!.AsObject();
        Assert.Equal("object", actionConfig["type"]!.GetValue<string>());
        Assert.Equal("json", actionConfig["widget"]!.GetValue<string>());
        Assert.False(actionConfig.ContainsKey("properties"));
    }

    [Fact]
    public async Task Enabled_live_provisions_one_paired_presence_with_default_heartbeat_config()
    {
        var fixture = new Fixture(live: true);

        await fixture.Heartbeat.ReconcileAsync();

        Assert.Single(fixture.OpenDiscussions("live"));
        Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        var automationId = Assert.Single(fixture.Automations.Entities).Id;
        var automation = await fixture.Entities.GetAsync(automationId)
            ?? throw new InvalidOperationException("Heartbeat automation is missing.");
        Assert.True(automation.Data["enabled"]!.GetValue<bool>());
        Assert.Equal(automation.Id.ToString(), fixture.Agent.Data[HeartbeatService.AutomationField]!.GetValue<string>());
        Assert.False(fixture.Agent.Data.ContainsKey("heartbeat"));
    }

    [Fact]
    public async Task Live_disable_is_a_reversible_pause_that_restores_the_same_paired_presence()
    {
        var fixture = new Fixture(live: true);
        await fixture.Heartbeat.ReconcileAsync();
        var originalAutomation = Assert.Single(fixture.Automations.Entities);
        var originalAutomationId = originalAutomation.Id;
        var originalSchedule = originalAutomation.Data["trigger"]!.DeepClone()!.ToJsonString();
        var originalQuality = await fixture.Heartbeat.GetConfigurationAsync(fixture.Agent)
            ?? throw new InvalidOperationException("Heartbeat configuration is missing.");
        var originalLiveIds = fixture.DiscussionsOf("live").Select(d => d.Id).ToHashSet();
        var originalHeartbeatIds = fixture.DiscussionsOf(HeartbeatService.DiscussionType).Select(d => d.Id).ToHashSet();

        fixture.Agent.Data["live"] = false;
        await fixture.Heartbeat.ReconcileAsync();

        Assert.Empty(fixture.OpenDiscussions("live"));
        Assert.Empty(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        Assert.All(fixture.DiscussionsOf("live").Concat(fixture.DiscussionsOf(HeartbeatService.DiscussionType)),
            discussion => Assert.True(DiscussionStatus.IsClosed(discussion.Data["status"]!.GetValue<string>())));
        var automation = Assert.Single(fixture.Automations.Entities);
        Assert.False(automation.Data["enabled"]!.GetValue<bool>());
        Assert.Equal("agent-live-disabled", automation.Data["paused_reason"]!.GetValue<string>());
        Assert.False(automation.Data.ContainsKey("archived_at"));
        Assert.Equal(originalSchedule, automation.Data["trigger"]!.ToJsonString());
        Assert.Equal(originalQuality.QualityTier, (await fixture.Heartbeat.GetConfigurationAsync(fixture.Agent))!.QualityTier);

        fixture.Agent.Data["live"] = true;
        await fixture.Heartbeat.ReconcileAsync();

        var restoredAutomation = await fixture.Entities.GetAsync(originalAutomationId)
            ?? throw new InvalidOperationException("Heartbeat automation vanished during re-enable.");
        Assert.Equal(originalAutomationId, restoredAutomation.Id);
        Assert.True(restoredAutomation.Data["enabled"]!.GetValue<bool>());
        Assert.False(restoredAutomation.Data.ContainsKey("paused_reason"));
        Assert.False(restoredAutomation.Data.ContainsKey("archived_at"));
        Assert.Equal(originalSchedule, restoredAutomation.Data["trigger"]!.ToJsonString());
        Assert.Equal(originalQuality.QualityTier, (await fixture.Heartbeat.GetConfigurationAsync(fixture.Agent))!.QualityTier);
        Assert.Equal(1, fixture.Automations.EnsureCalls);
        var restoredLive = Assert.Single(fixture.OpenDiscussions("live"));
        var restoredHeartbeat = Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        Assert.DoesNotContain(restoredLive.Id, originalLiveIds);
        Assert.DoesNotContain(restoredHeartbeat.Id, originalHeartbeatIds);
    }

    [Fact]
    public async Task Disabled_live_tick_skips_before_it_can_self_heal_a_heartbeat_discussion()
    {
        var fixture = new Fixture(live: false, heartbeat: new JsonObject());
        var automation = fixture.Automations.Add(fixture.Agent.Id, enabled: true);
        fixture.Agent.Data[HeartbeatService.AutomationField] = automation.Id.ToString();
        var handler = new HeartbeatTickHandler(fixture.Heartbeat, fixture.DiscussionStore,
            fixture.Entities, null!);

        var result = await handler.ExecuteAsync(new AutomationActionContext
        {
            Automation = automation,
            Beneficiary = new ComputeBeneficiary("system", Reason: "test"),
            AttemptJobId = Guid.NewGuid(),
            CorrelationId = "test",
        }, CancellationToken.None);

        Assert.True(result!["skipped"]!.GetValue<bool>());
        Assert.Contains("LIVE is disabled", result["summary"]!.GetValue<string>());
        Assert.Empty(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
    }

    [Fact]
    public async Task Reconciliation_is_idempotent_and_live_overrides_legacy_heartbeat_enabled_false()
    {
        var fixture = new Fixture(live: true, heartbeat: new JsonObject
        {
            ["enabled"] = false,
            ["schedule"] = "0 9 * * *",
            ["quality_tier"] = "standard",
            ["tick_wait_minutes"] = 22,
            ["max_turns"] = 31,
        });

        await fixture.Heartbeat.ReconcileAsync();
        await fixture.Heartbeat.ReconcileAsync();

        Assert.Equal(1, fixture.Automations.EnsureCalls);
        Assert.Single(fixture.OpenDiscussions("live"));
        Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        var automationId = Assert.Single(fixture.Automations.Entities).Id;
        var automation = await fixture.Entities.GetAsync(automationId)
            ?? throw new InvalidOperationException("Heartbeat automation is missing.");
        Assert.True(automation.Data["enabled"]!.GetValue<bool>());
        Assert.Equal("0 9 * * *", automation.Data["trigger"]!["expression"]!.GetValue<string>());
        Assert.Equal(automation.Id.ToString(), fixture.Agent.Data[HeartbeatService.AutomationField]!.GetValue<string>());
        Assert.False(fixture.Agent.Data.ContainsKey("heartbeat"));
        Assert.Equal(1, fixture.Entities.AgentReplaceDataCalls);
        var workflowId = Guid.Parse(automation.Data["workflow"]!["entity_id"]!.GetValue<string>());
        var actionConfig = fixture.Entities.All.Single(entity => entity.Id == workflowId)
            .Data["graph"]!["nodes"]![0]!["data"]!["config"]!["action_config"]!.AsObject();
        Assert.Equal(fixture.QualityTierId("standard").ToString(), actionConfig["quality_tier"]!.GetValue<string>());
        Assert.Equal(22, actionConfig["tick_wait_minutes"]!.GetValue<int>());
        Assert.Equal(31, actionConfig["max_turns"]!.GetValue<int>());
        Assert.Equal("standard", (await fixture.Heartbeat.GetConfigurationAsync(fixture.Agent))!.QualityTier);
    }

    [Fact]
    public async Task Live_preserves_a_pre_existing_non_live_automation_pause()
    {
        var fixture = new Fixture(live: true);
        var automation = fixture.Automations.Add(fixture.Agent.Id, enabled: false);
        automation.Data["paused_reason"] = "maintenance-window";
        fixture.Agent.Data[HeartbeatService.AutomationField] = automation.Id.ToString();

        await fixture.Heartbeat.ReconcileAsync();

        var persisted = await fixture.Entities.GetAsync(automation.Id)
            ?? throw new InvalidOperationException("Heartbeat automation is missing.");
        Assert.False(persisted.Data["enabled"]!.GetValue<bool>());
        Assert.Equal("maintenance-window", persisted.Data["paused_reason"]!.GetValue<string>());
        Assert.Equal(0, fixture.Automations.EnsureCalls);
        Assert.Single(fixture.OpenDiscussions("live"));
        Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
    }

    [Fact]
    public async Task Startup_reconciliation_resumes_both_infrastructure_stopped_presence_sessions_once()
    {
        var gateway = new RecoveryComputeGateway { StaleReadsAfterResume = 1 };
        var fixture = new Fixture(live: true, computeGateway: gateway);
        await fixture.Heartbeat.ReconcileAsync();
        var live = Assert.Single(fixture.OpenDiscussions("live"));
        var heartbeat = Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        await fixture.BindStoppedSessionAsync(live, "live-session", "maintenance_restart", gateway);
        await fixture.BindStoppedSessionAsync(heartbeat, "heartbeat-session", null, gateway);

        var result = await fixture.Heartbeat.ReconcileAsync();

        Assert.Equal(["live-session", "heartbeat-session"], gateway.ResumeRequests.Select(r => r.SessionId));
        Assert.All(gateway.ResumeRequests, request =>
        {
            Assert.NotNull(request.Provenance);
            Assert.Equal("agent", request.Provenance!.Actor.Kind);
            Assert.Equal("automation", request.Provenance.Origin.Entrypoint.Kind);
            Assert.Equal("presence:auto-resume", request.Provenance.Origin.Entrypoint.Route);
        });
        Assert.Equal(DiscussionStatus.Idle,
            (await fixture.DiscussionStore.GetAsync(live.Data["discussion_id"]!.GetValue<string>()))!.Status);
        Assert.Equal(DiscussionStatus.Idle,
            (await fixture.DiscussionStore.GetAsync(heartbeat.Data["discussion_id"]!.GetValue<string>()))!.Status);
        Assert.Equal(2, result["recoveredSessions"]!.AsArray().Count);

        await fixture.Heartbeat.ReconcileAsync();
        Assert.Equal(2, gateway.ResumeRequests.Count);
    }

    [Fact]
    public async Task Startup_reconciliation_preserves_explicit_presence_stop()
    {
        var gateway = new RecoveryComputeGateway();
        var fixture = new Fixture(live: true, computeGateway: gateway);
        await fixture.Heartbeat.ReconcileAsync();
        var live = Assert.Single(fixture.OpenDiscussions("live"));
        await fixture.BindStoppedSessionAsync(live, "live-session", "user_stopped", gateway);

        var result = await fixture.Heartbeat.ReconcileAsync();

        Assert.Empty(gateway.ResumeRequests);
        Assert.Empty(result["recoveredSessions"]!.AsArray());
        Assert.Equal(DiscussionStatus.Stopped,
            (await fixture.DiscussionStore.GetAsync(live.Data["discussion_id"]!.GetValue<string>()))!.Status);
    }

    [Fact]
    public async Task Heartbeat_tick_skips_an_explicitly_stopped_persistent_session()
    {
        var gateway = new RecoveryComputeGateway();
        var fixture = new Fixture(live: true, computeGateway: gateway);
        await fixture.Heartbeat.ReconcileAsync();
        var heartbeat = Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        await fixture.BindStoppedSessionAsync(
            heartbeat, "heartbeat-session", "user_stopped", gateway);
        var automation = Assert.Single(fixture.Automations.Entities);
        var handler = new HeartbeatTickHandler(
            fixture.Heartbeat, fixture.DiscussionStore, fixture.Entities, null!);

        var result = await handler.ExecuteAsync(new AutomationActionContext
        {
            Automation = automation,
            Beneficiary = new ComputeBeneficiary("system", Reason: "test"),
            AttemptJobId = Guid.NewGuid(),
            CorrelationId = "test",
        }, CancellationToken.None);

        Assert.True(result!["skipped"]!.GetValue<bool>());
        Assert.Contains("explicitly stopped", result["summary"]!.GetValue<string>());
        Assert.Empty(gateway.ResumeRequests);
    }

    [Fact]
    public async Task Startup_reconciliation_retries_when_compute_becomes_reachable_late()
    {
        var gateway = new RecoveryComputeGateway { UnavailableReadsRemaining = 1 };
        var fixture = new Fixture(live: true, computeGateway: gateway);
        await fixture.Heartbeat.ReconcileAsync();
        var heartbeat = Assert.Single(fixture.OpenDiscussions(HeartbeatService.DiscussionType));
        await fixture.BindStoppedSessionAsync(
            heartbeat, "heartbeat-session", "orphaned_on_restart", gateway);

        await fixture.Heartbeat.RunStartupReconciliationAsync(
            CancellationToken.None, [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Equal("heartbeat-session", Assert.Single(gateway.ResumeRequests).SessionId);
        Assert.Equal(DiscussionStatus.Idle,
            (await fixture.DiscussionStore.GetAsync(
                heartbeat.Data["discussion_id"]!.GetValue<string>()))!.Status);
    }

    private sealed class Fixture
    {
        public Fixture(bool live, JsonObject? heartbeat = null, IComputeGateway? computeGateway = null)
        {
            var deep = Entity("quality-tier", "deep", "Deep", new JsonObject());
            var standard = Entity("quality-tier", "standard", "Standard", new JsonObject());
            var agent = Entity("agent", "nova", "Nova", new JsonObject { ["live"] = live, ["quality_tier"] = deep.Id.ToString() });
            var plugin = Entity("plugin", "nova", "Nova", new JsonObject());
            if (heartbeat != null) agent.Data["heartbeat"] = heartbeat;
            Entities = new InMemoryEntityStore(agent, deep, standard, plugin);
            Discussions = new InMemoryDiscussions(Entities);
            DiscussionStore = new DiscussionStore(Entities, Discussions);
            Automations = new InMemoryAutomations(Entities);
            var compute = new RedComputeClient(computeGateway ?? new UnusedComputeGateway());
            var lifecycle = new DiscussionLifecycle(DiscussionStore, compute);
            var directory = new AgentDirectory(Entities, new InMemoryPluginEvents());
            Heartbeat = new HeartbeatService(Entities, Automations, Discussions, DiscussionStore,
                lifecycle, null!, compute, null!, directory, null!, NullLogger<HeartbeatService>.Instance);
        }

        public LeafEntity Agent => Entities.All.Single(entity => entity.TypeSlug == "agent");
        public InMemoryEntityStore Entities { get; }
        public InMemoryDiscussions Discussions { get; }
        public DiscussionStore DiscussionStore { get; }
        public InMemoryAutomations Automations { get; }
        public HeartbeatService Heartbeat { get; }

        public Guid QualityTierId(string slug) => Entities.All.Single(entity => entity.TypeSlug == "quality-tier" && entity.Slug == slug).Id;

        public IReadOnlyList<LeafEntity> OpenDiscussions(string type) => Entities.All
            .Where(e => e.TypeSlug == "discussion" && e.Data["type"]?.GetValue<string>() == type)
            .Where(e => !DiscussionStatus.IsClosed(e.Data["status"]?.GetValue<string>() ?? "stopped"))
            .ToList();

        public IReadOnlyList<LeafEntity> DiscussionsOf(string type) => Entities.All
            .Where(e => e.TypeSlug == "discussion" && e.Data["type"]?.GetValue<string>() == type)
            .ToList();

        public async Task BindStoppedSessionAsync(
            LeafEntity discussion, string sessionId, string? stopReason,
            RecoveryComputeGateway gateway)
        {
            await Entities.PatchAsync(discussion.Id, new JsonObject
            {
                ["session_id"] = sessionId,
                ["status"] = DiscussionStatus.Stopped,
            });
            gateway.SetSession(sessionId, "Stopped", stopReason, $"provider-{sessionId}");
        }
    }

    private sealed class InMemoryAutomations(InMemoryEntityStore store) : IWorkflowAutomations
    {
        public List<LeafEntity> Entities { get; } = [];
        public int EnsureCalls { get; private set; }

        public Task<LeafEntity> EnsureAsync(WorkflowAutomationDefinition definition, CancellationToken ct = default)
        {
            EnsureCalls++;
            var existing = Entities.SingleOrDefault(e => e.Slug == definition.Slug);
            if (existing != null) return Task.FromResult(existing);
            return Task.FromResult(Add(definition));
        }

        public Task<LeafEntity> UpsertAsync(WorkflowAutomationDefinition definition, CancellationToken ct = default)
        {
            EnsureCalls++;
            var existing = Entities.SingleOrDefault(e => e.Slug == definition.Slug);
            if (existing == null) return Task.FromResult(Add(definition));
            var workflowId = Guid.Parse(existing.Data["workflow"]!["entity_id"]!.GetValue<string>());
            var workflow = store.All.Single(entity => entity.Id == workflowId);
            workflow.Data["graph"] = Graph(definition);
            existing.Data["enabled"] = definition.Enabled;
            existing.Data["trigger"] = definition.Trigger.DeepClone();
            return Task.FromResult(existing);
        }

        public LeafEntity Add(Guid agentId, bool enabled) => Add(new WorkflowAutomationDefinition
        {
            Slug = "system-heartbeat-nova", Name = "test", NodeType = HeartbeatTickHandler.Type,
            Enabled = enabled, Trigger = new JsonObject { ["expression"] = HeartbeatConfig.DefaultSchedule },
            ExecutionPolicy = new JsonObject(), Ownership = new JsonObject { ["beneficiary"] = new JsonObject() },
            NodeContext = new JsonObject { ["agent"] = agentId.ToString() }, ActionConfig = new JsonObject
            {
                ["quality_tier"] = store.All.Single(entity => entity.TypeSlug == "quality-tier" && entity.Slug == "deep").Id.ToString(),
                ["tick_wait_minutes"] = 15, ["max_turns"] = 15,
            },
        });

        private LeafEntity Add(WorkflowAutomationDefinition definition)
        {
            var workflow = Entity("flow", $"{definition.Slug}-flow", "Heartbeat flow", new JsonObject { ["graph"] = Graph(definition) });
            store.Add(workflow);
            var entity = Entity("automation", definition.Slug, definition.Slug, new JsonObject
            {
                ["agent"] = definition.NodeContext["agent"]!.GetValue<string>(),
                ["enabled"] = definition.Enabled,
                ["trigger"] = definition.Trigger.DeepClone(),
                ["workflow"] = new JsonObject { ["entity_id"] = workflow.Id.ToString() },
            });
            Entities.Add(entity);
            store.Add(entity);
            return entity;
        }

        private static JsonObject Graph(WorkflowAutomationDefinition definition) => new()
        {
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["id"] = "action", ["data"] = new JsonObject { ["config"] = new JsonObject { ["action_config"] = definition.ActionConfig.DeepClone() } } },
            },
        };
    }

    private sealed class InMemoryEntityStore(params LeafEntity[] initial) : IEntityStore
    {
        private readonly List<LeafEntity> entities = [.. initial];
        public IReadOnlyList<LeafEntity> All => entities;
        public int ReplaceDataCalls { get; private set; }
        public int AgentReplaceDataCalls { get; private set; }

        public void Add(LeafEntity entity) => entities.Add(entity);

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(e => e.Id == id));

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(e => e.Slug == slug));

        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(e => e.TypeSlug == typeSlug && e.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            var matches = entities.Where(e => e.TypeSlug == query.TypeSlug)
                .Where(e => Matches(e.Data, query.DataEquals))
                .Skip(query.Offset).Take(query.Limit).ToList();
            return Task.FromResult<IReadOnlyList<LeafEntity>>(matches);
        }

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
        {
            var entity = Entity(typeSlug, Guid.NewGuid().ToString("N"), name, data ?? []);
            entities.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
        {
            var existing = entities.SingleOrDefault(e => e.TypeSlug == typeSlug && e.Slug == slug);
            if (existing != null) return Task.FromResult(existing);
            var entity = Entity(typeSlug, slug, name, data ?? []);
            entities.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            var index = entities.FindIndex(e => e.Id == id);
            var existing = entities[index];
            foreach (var (key, value) in patch)
                existing.Data[key] = value?.DeepClone();
            var updated = existing with { Name = name ?? existing.Name, UpdatedAt = DateTimeOffset.UtcNow };
            entities[index] = updated;
            return Task.FromResult(updated);
        }

        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
        {
            ReplaceDataCalls++;
            var index = entities.FindIndex(e => e.Id == id);
            if (entities[index].TypeSlug == "agent") AgentReplaceDataCalls++;
            var updated = entities[index] with { Data = data, Name = name ?? entities[index].Name, UpdatedAt = DateTimeOffset.UtcNow };
            entities[index] = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            entities.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        private static bool Matches(JsonObject data, IReadOnlyDictionary<string, object?>? expected)
            => expected == null || expected.All(pair => data[pair.Key]?.ToJsonString() == JsonSerializer.Serialize(pair.Value));
    }

    private sealed class InMemoryDiscussions(InMemoryEntityStore store) : IDiscussions
    {
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default)
        {
            var merged = data?.DeepClone() as JsonObject ?? [];
            merged["agent"] = agentSlugOrId;
            merged["status"] ??= DiscussionStatus.Idle;
            merged["created_at"] ??= DateTimeOffset.UtcNow.ToString("O");
            merged["last_activity"] ??= DateTimeOffset.UtcNow.ToString("O");
            var id = merged["discussion_id"]!.GetValue<string>();
            var entity = Entity("discussion", DiscussionStore.Slug(id), title ?? $"Discussion {id}", merged);
            store.Add(entity);
            return Task.FromResult(entity);
        }

        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new NoopDisposable();
    }

    private sealed class UnusedComputeGateway : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
            => throw new InvalidOperationException("This test must not contact RedCompute.");
    }

    private sealed class RecoveryComputeGateway : IComputeGateway
    {
        private readonly Dictionary<string, SessionState> sessions = [];
        public List<(string SessionId, ComputeProvenance? Provenance)> ResumeRequests { get; } = [];
        public int UnavailableReadsRemaining { get; set; }
        public int StaleReadsAfterResume { get; set; }

        public void SetSession(string id, string status, string? stopReason, string providerSessionId)
            => sessions[id] = new(status, stopReason, providerSessionId);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.AbsolutePath
                : request.RequestUri?.OriginalString ?? "";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var sessionId = parts.Length >= 3 ? parts[2] : "";
            if (!sessions.TryGetValue(sessionId, out var session))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            if (request.Method == HttpMethod.Get && UnavailableReadsRemaining > 0)
            {
                UnavailableReadsRemaining--;
                return Task.FromResult(new HttpResponseMessage(
                    System.Net.HttpStatusCode.ServiceUnavailable));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/resume", StringComparison.Ordinal))
            {
                ResumeRequests.Add((sessionId, provenance));
                sessions[sessionId] = session with { Status = "Idle", StopReason = null };
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Get
                && ResumeRequests.Any(request => request.SessionId == sessionId)
                && StaleReadsAfterResume > 0)
            {
                StaleReadsAfterResume--;
                session = session with
                {
                    Status = "Stopped",
                    StopReason = "maintenance_restart",
                };
            }

            var payload = JsonSerializer.Serialize(new
            {
                session = new
                {
                    id = sessionId,
                    status = session.Status,
                    stopReason = session.StopReason,
                    providerSessionId = session.ProviderSessionId,
                },
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        private sealed record SessionState(string Status, string? StopReason, string ProviderSessionId);
    }

    private sealed class InMemoryPluginEvents : IPluginEvents
    {
        public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
            => Task.CompletedTask;

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler)
            => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    private static LeafEntity Entity(string type, string slug, string name, JsonObject data) => new(
        Guid.NewGuid(), type, slug, name, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
}
