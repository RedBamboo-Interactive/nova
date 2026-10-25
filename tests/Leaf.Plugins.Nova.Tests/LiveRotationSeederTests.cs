using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// The plugin cutover dropped the standalone app's daily LIVE rotation. It is seeded once
/// per installation; after that it belongs to the user (disable, edit, archive, remove).
/// </summary>
public sealed class LiveRotationSeederTests
{
    [Fact]
    public async Task Fresh_install_seeds_exactly_one_enabled_daily_rotation()
    {
        var fixture = new Fixture();

        var seeded = await fixture.Seeder.EnsureAsync(fixture.Agent);

        var automation = Assert.Single(fixture.Automations());
        Assert.Equal(automation.Id, seeded!.Id);
        Assert.Equal(LiveRotationSeeder.Slug, automation.Slug);
        Assert.Equal("system:live-rotation", automation.Name);
        Assert.True(automation.Data["enabled"]!.GetValue<bool>());
        Assert.Equal("30 3 * * *", automation.Data["trigger"]!["expression"]!.GetValue<string>());
        Assert.Equal("UTC", automation.Data["trigger"]!["timezone"]!.GetValue<string>());
        Assert.Equal(1, fixture.Workflows.EnsureCalls);
        Assert.Equal(automation.Id.ToString(),
            fixture.Agent.Data[LiveRotationSeeder.ReferenceField]!.GetValue<string>());
    }

    [Fact]
    public async Task Seed_posts_to_the_live_alias_rotate_endpoint_as_the_sole_user()
    {
        var fixture = new Fixture();

        await fixture.Seeder.EnsureAsync(fixture.Agent);

        var graph = fixture.Workflows.LastDefinition!.WorkflowGraph!;
        var action = graph["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "action")!;
        Assert.Equal("http-request", action["type"]!.GetValue<string>());
        var config = action["data"]!["config"]!;
        Assert.Equal("POST", config["method"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:18804/api/apps/nova/discussions/live/rotate",
            config["url"]!.GetValue<string>());
        var automation = Assert.Single(fixture.Automations());
        Assert.Equal(fixture.User.Id.ToString(),
            automation.Data["ownership"]!["beneficiary"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Running_again_never_duplicates_it()
    {
        var fixture = new Fixture();

        await fixture.Seeder.EnsureAsync(fixture.Agent);
        var second = await fixture.Seeder.EnsureAsync(fixture.Agent);
        var third = await fixture.Seeder.EnsureAsync(fixture.Entities.Items.Single(e => e.TypeSlug == "agent"));

        Assert.Null(second);
        Assert.Null(third);
        Assert.Single(fixture.Automations());
        Assert.Equal(1, fixture.Workflows.EnsureCalls);
    }

    [Fact]
    public async Task Carried_over_entity_is_adopted_untouched()
    {
        var fixture = new Fixture();
        var carried = fixture.AddAutomation(LiveRotationSeeder.Slug, enabled: true,
            new JsonObject { ["expression"] = "30 3 * * *", ["legacy_utc_expression"] = "30 3 * * *" });
        var before = carried.Data.ToJsonString();

        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.Equal(0, fixture.Workflows.EnsureCalls);
        var automation = Assert.Single(fixture.Automations());
        Assert.Equal(carried.Id, automation.Id);
        Assert.Equal(before, automation.Data.ToJsonString());
        Assert.Equal(carried.Id.ToString(),
            fixture.Agent.Data[LiveRotationSeeder.ReferenceField]!.GetValue<string>());
    }

    [Fact]
    public async Task Disabled_automation_stays_disabled()
    {
        var fixture = new Fixture();
        var carried = fixture.AddAutomation(LiveRotationSeeder.Slug, enabled: false, new JsonObject());

        await fixture.Seeder.EnsureAsync(fixture.Agent);
        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.False(Assert.Single(fixture.Automations()).Data["enabled"]!.GetValue<bool>());
        Assert.Equal(carried.Id, Assert.Single(fixture.Automations()).Id);
    }

    [Fact]
    public async Task Seeded_then_disabled_by_the_user_stays_disabled()
    {
        var fixture = new Fixture();
        await fixture.Seeder.EnsureAsync(fixture.Agent);
        fixture.Automations().Single().Data["enabled"] = false;

        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.False(Assert.Single(fixture.Automations()).Data["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Archived_automation_is_not_recreated()
    {
        var fixture = new Fixture();
        var archived = fixture.AddAutomation(LiveRotationSeeder.Slug, enabled: false, new JsonObject());
        archived.Data["archived_at"] = DateTimeOffset.UtcNow.ToString("O");

        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.Equal(0, fixture.Workflows.EnsureCalls);
        Assert.Single(fixture.Automations());
        Assert.NotNull(fixture.Automations().Single().Data["archived_at"]);
    }

    [Fact]
    public async Task Removed_after_seeding_is_not_recreated()
    {
        var fixture = new Fixture();
        await fixture.Seeder.EnsureAsync(fixture.Agent);
        fixture.Entities.Items.RemoveAll(e => e.TypeSlug == "automation");

        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.Empty(fixture.Automations());
        Assert.Equal(1, fixture.Workflows.EnsureCalls);
    }

    [Fact]
    public async Task No_user_yet_defers_the_seed_until_one_exists()
    {
        var fixture = new Fixture(withUser: false);

        Assert.Null(await fixture.Seeder.EnsureAsync(fixture.Agent));
        Assert.Empty(fixture.Automations());
        Assert.False(fixture.Agent.Data.ContainsKey(LiveRotationSeeder.ReferenceField));

        fixture.AddUser();
        await fixture.Seeder.EnsureAsync(fixture.Agent);

        Assert.Single(fixture.Automations());
    }

    private sealed class Fixture
    {
        public MemoryEntities Entities { get; } = new();
        public RecordingWorkflows Workflows { get; }
        public LiveRotationSeeder Seeder { get; }
        public LeafEntity User { get; private set; } = null!;

        public Fixture(bool withUser = true)
        {
            Entities.Items.Add(Entity("agent", "nova", "Nova", new JsonObject { ["live"] = true }));
            Workflows = new RecordingWorkflows(Entities);
            Seeder = new LiveRotationSeeder(Entities, Workflows, NullLogger<LiveRotationSeeder>.Instance);
            if (withUser) AddUser();
        }

        public LeafEntity Agent => Entities.Items.Single(e => e.TypeSlug == "agent");

        public void AddUser() => Entities.Items.Add(User = Entity("user", "laurent", "Laurent", new JsonObject()));

        public IReadOnlyList<LeafEntity> Automations()
            => Entities.Items.Where(e => e.TypeSlug == "automation").ToList();

        public LeafEntity AddAutomation(string slug, bool enabled, JsonObject trigger)
        {
            var entity = Entity("automation", slug, "system:live-rotation", new JsonObject
            {
                ["enabled"] = enabled,
                ["trigger"] = trigger,
            });
            Entities.Items.Add(entity);
            return entity;
        }
    }

    /// <summary>Mirrors the kernel provisioner: Ensure returns an existing slug unchanged.</summary>
    private sealed class RecordingWorkflows(MemoryEntities entities) : IWorkflowAutomations
    {
        public int EnsureCalls { get; private set; }
        public WorkflowAutomationDefinition? LastDefinition { get; private set; }

        public Task<LeafEntity> EnsureAsync(WorkflowAutomationDefinition definition, CancellationToken ct = default)
        {
            EnsureCalls++;
            LastDefinition = definition;
            var existing = entities.Items.SingleOrDefault(e => e.TypeSlug == "automation" && e.Slug == definition.Slug);
            if (existing is not null) return Task.FromResult(existing);
            var entity = Entity("automation", definition.Slug, definition.Name, new JsonObject
            {
                ["enabled"] = definition.Enabled,
                ["trigger"] = definition.Trigger.DeepClone(),
                ["ownership"] = definition.Ownership.DeepClone(),
            });
            entities.Items.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<LeafEntity> UpsertAsync(WorkflowAutomationDefinition definition, CancellationToken ct = default)
            => throw new NotSupportedException("Seeding must never overwrite an existing automation.");
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafEntity>>(
                Items.Where(entity => entity.TypeSlug == query.TypeSlug).Take(query.Limit).ToList());
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            var index = Items.FindIndex(entity => entity.Id == id);
            foreach (var (key, value) in patch) Items[index].Data[key] = value?.DeepClone();
            return Task.FromResult(Items[index]);
        }
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static LeafEntity Entity(string type, string slug, string name, JsonObject data) => new(
        Guid.NewGuid(), type, slug, name, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
}
